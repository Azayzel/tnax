using System.Net;
using System.Text;
using System.Xml.Linq;
using Tnax.Core;

namespace Tnax.Core.Tests;

public sealed class WebDavClientTests
{
    private static readonly NasEndpoint Endpoint = NasEndpoint.Parse("https://nas.example/Photos/");

    [Fact]
    public async Task ProbeRequiresActualWebDavCollection()
    {
        using var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("PROPFIND", request.Method.Method);
            Assert.Equal("0", Assert.Single(request.Headers.GetValues("Depth")));
            Assert.Equal("Basic", request.Headers.Authorization?.Scheme);
            return Metadata("/Photos/", true);
        });
        using var client = Client(handler);
        await client.ProbeAsync();
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(200, "<html>Login</html>", NasError.Protocol)]
    [InlineData(207, "<html>Login</html>", NasError.Protocol)]
    [InlineData(207, "not xml", NasError.Protocol)]
    [InlineData(401, "", NasError.Authentication)]
    [InlineData(403, "", NasError.Permission)]
    [InlineData(302, "", NasError.Protocol)]
    public async Task RejectsMisleadingOrFailedResponses(int status, string body, NasError expected)
    {
        using var handler = new ScriptedHandler(_ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
        using var client = Client(handler);
        var error = await Assert.ThrowsAsync<NasException>(() => client.ProbeAsync());
        Assert.Equal(expected, error.Code);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task RejectsMetadataForAnotherDestination()
    {
        using var handler = new ScriptedHandler(_ => Metadata("https://other.example/Photos/", true));
        using var client = Client(handler);
        Assert.Equal(NasError.Protocol, (await Assert.ThrowsAsync<NasException>(() => client.ProbeAsync())).Code);
    }

    [Fact]
    public async Task CreatesOnlyThenVerifiesActualBytes()
    {
        var bytes = Encoding.UTF8.GetBytes("original photograph");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            using var handler = new ScriptedHandler(
                _ => new(HttpStatusCode.NotFound),
                request =>
                {
                    Assert.Equal(HttpMethod.Put, request.Method);
                    Assert.Equal("*", Assert.Single(request.Headers.GetValues("If-None-Match")));
                    Assert.Equal(bytes.Length, request.Content?.Headers.ContentLength);
                    return new(HttpStatusCode.Created);
                },
                request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
                });
            using var client = Client(handler);
            var stages = new List<TransferStage>();
            var receipt = await client.UploadFileAsync(path, ["image.heic"], stage => { stages.Add(stage); return Task.CompletedTask; });
            Assert.Equal(bytes.Length, receipt.Length);
            Assert.False(receipt.AlreadyPresent);
            Assert.Equal(64, receipt.Sha256.Length);
            Assert.Equal(3, handler.Calls);
            Assert.Equal([TransferStage.Hashing, TransferStage.Uploading, TransferStage.Verifying], stages);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("original photograph", true)]
    [InlineData("different contents!", false)]
    public async Task ExistingFilesAreVerifiedNeverOverwritten(string remote, bool matches)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "original photograph");
            using var handler = new ScriptedHandler(
                _ => Metadata("/Photos/image.heic", false, 19),
                request =>
                {
                    Assert.Equal(HttpMethod.Get, request.Method);
                    return new(HttpStatusCode.OK) { Content = new StringContent(remote) };
                });
            using var client = Client(handler);
            if (matches)
                Assert.True((await client.UploadFileAsync(path, ["image.heic"])).AlreadyPresent);
            else
                Assert.Equal(NasError.Conflict, (await Assert.ThrowsAsync<NasException>(() => client.UploadFileAsync(path, ["image.heic"]))).Code);
            Assert.Equal(2, handler.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ChangedStagedOriginalIsRejectedBeforeNetworkAccess()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, "changed original");
            using var handler = new ScriptedHandler(_ => throw new InvalidOperationException("Must not send"));
            using var client = Client(handler);
            var error = await Assert.ThrowsAsync<NasException>(() => client.UploadFileAsync(path, ["image.heic"], expectedSha256: new string('0', 64)));
            Assert.Equal(NasError.Conflict, error.Code);
            Assert.Equal(0, handler.Calls);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RefusesToSendCredentialsOutsideConfiguredFolder()
    {
        using var handler = new ScriptedHandler(_ => throw new InvalidOperationException("Must not send"));
        using var client = Client(handler);
        Assert.Equal(NasError.Address, (await Assert.ThrowsAsync<NasException>(() =>
            client.InspectAsync(new Uri("https://other.example/Photos/")))).Code);
        Assert.Equal(0, handler.Calls);
    }

    private static WebDavClient Client(HttpMessageHandler handler) => new(Endpoint, "test-user", "test-password", handler);

    [Fact]
    public async Task ListsOnlyImmediateFoldersWithinTheEndpoint()
    {
        XNamespace dav = "DAV:";
        var entries = new[]
        {
            ("/Photos/", true), ("/Photos/Family%20Photos/", true), ("/Photos/Family%20Photos/", true),
            ("/Photos/image.jpg", false), ("/Photos/nested/child/", true),
            ("https://other.example/Photos/Stolen/", true), ("/Other/", true),
            ("/Photos/encoded%2Fchild/", true), ("/Photos/%252e%252e/", true)
        };
        var xml = new XElement(dav + "multistatus", entries.Select(entry => new XElement(dav + "response",
            new XElement(dav + "href", entry.Item1), new XElement(dav + "propstat",
                new XElement(dav + "prop", new XElement(dav + "resourcetype", entry.Item2 ? new XElement(dav + "collection") : null)),
                new XElement(dav + "status", "HTTP/1.1 200 OK")))));
        using var handler = new ScriptedHandler(request =>
        {
            Assert.Equal("PROPFIND", request.Method.Method);
            Assert.Equal("1", Assert.Single(request.Headers.GetValues("Depth")));
            return new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString()) };
        });
        using var client = Client(handler);
        var folder = Assert.Single(await client.ListFoldersAsync());
        Assert.Equal("Family Photos", folder.Name);
        Assert.Equal("https://nas.example/Photos/Family%20Photos/", folder.Address.AbsoluteUri);
    }

    [Theory]
    [InlineData(200, "<html>Login</html>")]
    [InlineData(207, "not XML")]
    [InlineData(207, "<!DOCTYPE data [<!ENTITY secret SYSTEM 'file:///private'>]><data>&secret;</data>")]
    public async Task FolderListingRejectsNonWebDavAndUnsafeXml(int status, string content)
    {
        using var handler = new ScriptedHandler(_ => new((HttpStatusCode)status) { Content = new StringContent(content) });
        using var client = Client(handler);
        Assert.Equal(NasError.Protocol, (await Assert.ThrowsAsync<NasException>(() => client.ListFoldersAsync())).Code);
    }

    private static HttpResponseMessage Metadata(string href, bool collection, long? length = null)
    {
        XNamespace dav = "DAV:";
        var xml = new XElement(dav + "multistatus", new XElement(dav + "response",
            new XElement(dav + "href", href), new XElement(dav + "propstat",
                new XElement(dav + "prop", new XElement(dav + "resourcetype", collection ? new XElement(dav + "collection") : null),
                    length is null ? null : new XElement(dav + "getcontentlength", length)),
                new XElement(dav + "status", "HTTP/1.1 200 OK"))));
        return new(HttpStatusCode.MultiStatus) { Content = new StringContent(xml.ToString(), Encoding.UTF8, "application/xml") };
    }

    private sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.True(Calls < steps.Length, "Unexpected extra HTTP request");
            return Task.FromResult(steps[Calls++](request));
        }
    }
}
