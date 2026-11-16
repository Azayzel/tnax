using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Tnax.Core;

public enum NasError { Address, Authentication, Permission, MissingFolder, Conflict, Protocol, Connection, Certificate }
public enum TransferStage { Hashing, Uploading, Verifying }
public sealed record RemoteResource(bool IsCollection, long? Length);
public sealed record RemoteFolder(string Name, Uri Address);
public sealed record UploadReceipt(Uri Destination, long Length, string Sha256, bool AlreadyPresent);

public sealed class NasException(NasError code, string message) : Exception(message)
{
    public NasError Code { get; } = code;
}

public sealed class WebDavClient : IDisposable
{
    private static readonly XNamespace Dav = "DAV:";
    private readonly HttpClient http;
    private readonly AuthenticationHeaderValue authorization;
    public NasEndpoint Endpoint { get; }

    public WebDavClient(NasEndpoint endpoint, string username, string password, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(username) || username.Contains(':') || username.Any(char.IsControl))
            throw new ArgumentException("Enter a valid NAS username.");
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Enter your NAS password.");
        Endpoint = endpoint;
        authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(15)
        }) { Timeout = TimeSpan.FromHours(2) };
    }

    public async Task ProbeAsync(CancellationToken cancellationToken = default)
    {
        var resource = await InspectAsync(Endpoint.BaseUri, cancellationToken);
        if (resource is not { IsCollection: true })
            throw new NasException(NasError.MissingFolder, "The WebDAV backup folder does not exist or is not a folder.");
    }

    public async Task<IReadOnlyList<RemoteFolder>> ListFoldersAsync(CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = Request(new HttpMethod("PROPFIND"), Endpoint.BaseUri);
        request.Headers.Add("Depth", "1");
        request.Content = new StringContent(
            new XDocument(new XElement(Dav + "propfind", new XElement(Dav + "prop",
                new XElement(Dav + "resourcetype"), new XElement(Dav + "displayname")))).ToString(),
            Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, deadline.Token);
        Require(response, HttpStatusCode.MultiStatus);
        var document = await ReadMultistatusAsync(response, deadline.Token);
        var folders = new List<RemoteFolder>();
        foreach (var entry in document.Root!.Elements(Dav + "response"))
        {
            if (!Uri.TryCreate(Endpoint.BaseUri, entry.Element(Dav + "href")?.Value, out var address) ||
                !Endpoint.Contains(address) || SameResource(Endpoint.BaseUri, address))
                continue;
            var relative = Endpoint.BaseUri.MakeRelativeUri(address).OriginalString.TrimEnd('/');
            var segment = Uri.UnescapeDataString(relative);
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(['/', '\\', '%']) >= 0 || segment.Any(char.IsControl))
                continue;
            if (Status(entry.Element(Dav + "status")?.Value) >= 400)
                continue;
            var properties = entry.Elements(Dav + "propstat")
                .Where(element => Status(element.Element(Dav + "status")?.Value) == 200)
                .SelectMany(element => element.Elements(Dav + "prop")).ToArray();
            if (!properties.SelectMany(element => element.Elements(Dav + "resourcetype"))
                .Any(element => element.Element(Dav + "collection") is not null))
                continue;
            var name = properties.SelectMany(element => element.Elements(Dav + "displayname")).FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl))
                name = segment;
            folders.Add(new RemoteFolder(name, new Uri(Endpoint.Resource(segment).AbsoluteUri + "/")));
        }
        return folders.DistinctBy(folder => folder.Address).OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<RemoteResource?> InspectAsync(Uri destination, CancellationToken cancellationToken = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(25));
        using var request = Request(new HttpMethod("PROPFIND"), destination);
        request.Headers.Add("Depth", "0");
        request.Content = new StringContent(
            new XDocument(new XElement(Dav + "propfind", new XElement(Dav + "prop",
                new XElement(Dav + "resourcetype"), new XElement(Dav + "getcontentlength")))).ToString(),
            Encoding.UTF8, "application/xml");
        using var response = await SendAsync(request, deadline.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        Require(response, HttpStatusCode.MultiStatus);

        var document = await ReadMultistatusAsync(response, deadline.Token);
        var match = document.Root!.Elements(Dav + "response").FirstOrDefault(element =>
            Uri.TryCreate(destination, element.Element(Dav + "href")?.Value, out var href) && SameResource(destination, href));
        if (match is null)
            throw Protocol();
        var resourceStatus = Status(match.Element(Dav + "status")?.Value);
        if (resourceStatus == 404)
            return null;
        if (resourceStatus >= 400)
            throw Failure((HttpStatusCode)resourceStatus);
        var properties = match.Elements(Dav + "propstat")
            .Where(element => Status(element.Element(Dav + "status")?.Value) == 200)
            .SelectMany(element => element.Elements(Dav + "prop")).ToArray();
        var resourceType = properties.SelectMany(element => element.Elements(Dav + "resourcetype")).FirstOrDefault();
        if (resourceType is null)
            throw Protocol();
        var lengthText = properties.SelectMany(element => element.Elements(Dav + "getcontentlength")).FirstOrDefault()?.Value;
        long? length = long.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        return new RemoteResource(resourceType.Element(Dav + "collection") is not null, length);
    }

    private static async Task<XDocument> ReadMultistatusAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
                MaxCharactersInDocument = 1024 * 1024
            });
            var document = await XDocument.LoadAsync(reader, LoadOptions.None, cancellationToken);
            if (document.Root?.Name != Dav + "multistatus")
                throw Protocol();
            return document;
        }
        catch (XmlException)
        {
            throw Protocol();
        }
    }

    public async Task EnsureFoldersAsync(string[] segments, CancellationToken cancellationToken = default)
    {
        for (var count = 1; count <= segments.Length; count++)
        {
            var destination = new Uri(Endpoint.Resource(segments[..count]).AbsoluteUri + "/");
            var existing = await InspectAsync(destination, cancellationToken);
            if (existing is { IsCollection: true })
                continue;
            if (existing is not null)
                throw Conflict();
            using var request = Request(new HttpMethod("MKCOL"), destination);
            using var response = await SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
                Require(response, HttpStatusCode.Created);
            if (await InspectAsync(destination, cancellationToken) is not { IsCollection: true })
                throw new NasException(NasError.MissingFolder, "The NAS did not confirm the new backup folder.");
        }
    }

    public async Task<UploadReceipt> UploadFileAsync(string localPath, string[] segments,
        Func<TransferStage, Task>? progress = null, CancellationToken cancellationToken = default, string? expectedSha256 = null)
    {
        var destination = Endpoint.Resource(segments);
        await ReportAsync(progress, TransferStage.Hashing);
        await using var source = new FileStream(localPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = source.Length;
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(source, cancellationToken));
        if (expectedSha256 is not null && !hash.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new NasException(NasError.Conflict, "The staged original changed. Re-import it before backup; no upload was attempted.");
        source.Position = 0;
        var existing = await InspectAsync(destination, cancellationToken);
        if (existing is not null)
        {
            if (existing.IsCollection || existing.Length != length)
                throw Conflict();
            await ReportAsync(progress, TransferStage.Verifying);
            await VerifyAsync(destination, length, hash, cancellationToken);
            return new UploadReceipt(destination, length, hash, true);
        }

        await ReportAsync(progress, TransferStage.Uploading);
        using var request = Request(HttpMethod.Put, destination);
        request.Headers.TryAddWithoutValidation("If-None-Match", "*");
        request.Content = new StreamContent(source);
        request.Content.Headers.ContentLength = length;
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await SendAsync(request, cancellationToken);
        var raced = response.StatusCode == HttpStatusCode.PreconditionFailed;
        if (!raced)
            Require(response, HttpStatusCode.Created);
        await ReportAsync(progress, TransferStage.Verifying);
        await VerifyAsync(destination, length, hash, cancellationToken);
        return new UploadReceipt(destination, length, hash, raced);
    }

    public async Task VerifyAsync(Uri destination, long expectedLength, string expectedHash, CancellationToken cancellationToken = default)
    {
        using var request = Request(HttpMethod.Get, destination);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
        using var response = await SendAsync(request, cancellationToken);
        Require(response, HttpStatusCode.OK);
        if (response.Content.Headers.ContentLength is long length && length != expectedLength)
            throw Conflict();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long received = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            received += read;
            if (received > expectedLength)
                throw Conflict();
            hash.AppendData(buffer, 0, read);
        }
        if (received != expectedLength || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw Conflict();
    }

    private HttpRequestMessage Request(HttpMethod method, Uri destination)
    {
        if (!Endpoint.Contains(destination))
            throw new NasException(NasError.Address, "The destination is outside the configured NAS folder.");
        var request = new HttpRequestMessage(method, destination);
        request.Headers.Authorization = authorization;
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw exception.HttpRequestError == HttpRequestError.SecureConnectionError
                ? new NasException(NasError.Certificate, "HTTPS could not be verified. Use a trusted certificate matching the NAS hostname.")
                : new NasException(NasError.Connection, "Cannot reach the WebDAV service. Check its address and your Wi-Fi or VPN connection.");
        }
    }

    private static bool SameResource(Uri expected, Uri actual) =>
        expected.Scheme == actual.Scheme && expected.Host == actual.Host && expected.Port == actual.Port &&
        string.IsNullOrEmpty(actual.UserInfo) && string.IsNullOrEmpty(actual.Query) && string.IsNullOrEmpty(actual.Fragment) &&
        Uri.UnescapeDataString(expected.AbsolutePath).TrimEnd('/') == Uri.UnescapeDataString(actual.AbsolutePath).TrimEnd('/');

    private static int Status(string? status) =>
        int.TryParse(status?.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1), out var code) ? code : 0;

    private static Task ReportAsync(Func<TransferStage, Task>? progress, TransferStage stage) => progress?.Invoke(stage) ?? Task.CompletedTask;

    private static void Require(HttpResponseMessage response, HttpStatusCode expected)
    {
        if (response.StatusCode != expected)
            throw Failure(response.StatusCode);
    }

    private static NasException Failure(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => new(NasError.Authentication, "WebDAV rejected the credentials. Check the NAS account and WebDAV access."),
        HttpStatusCode.Forbidden => new(NasError.Permission, "This NAS account does not have permission for the backup folder."),
        HttpStatusCode.NotFound or HttpStatusCode.Conflict => new(NasError.MissingFolder, "The WebDAV destination was not found. Check the folder address."),
        HttpStatusCode.PreconditionFailed => Conflict(),
        _ when (int)status is >= 300 and < 400 => new(NasError.Protocol, "The NAS redirected the request. Enter its final HTTPS WebDAV folder address."),
        _ => new(NasError.Protocol, $"Unexpected WebDAV response ({(int)status}). Check that this is the file service, not the TOS login page.")
    };

    private static NasException Protocol() => new(NasError.Protocol, "The server did not return valid WebDAV metadata for this folder.");
    private static NasException Conflict() => new(NasError.Conflict, "The remote file does not match the original. Nothing was replaced; review this conflict.");
    public void Dispose() => http.Dispose();
}
