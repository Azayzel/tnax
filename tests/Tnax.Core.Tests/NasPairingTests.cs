using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Tnax.Core;

namespace Tnax.Core.Tests;

public sealed class NasPairingTests
{
    private static readonly Uri Server = new("https://192.168.0.87:474/Photos/");

    [Fact]
    public void PairingRoundTripsOnlyPublicSetupData()
    {
        using var authority = Authority();
        var pairing = Pair(authority);
        var json = pairing.Export();
        Assert.Equal(pairing, NasPairing.Parse(json));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(["version", "name", "serverAddress", "rootCertificate"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.DoesNotContain("PRIVATE KEY", Encoding.UTF8.GetString(json));
        using var publicRoot = pairing.LoadRootCertificate();
        Assert.False(publicRoot.HasPrivateKey);
    }

    [Fact]
    public void AcceptsPublicPemCertificateExports()
    {
        using var authority = Authority();
        var pairing = NasPairing.Create("Family NAS", Server.AbsoluteUri, Encoding.UTF8.GetBytes(authority.ExportCertificatePem()));
        Assert.Equal(Pair(authority).RootCertificate, pairing.RootCertificate);
    }

    [Fact]
    public void TrustIsLimitedToThePairedServerAndAuthority()
    {
        using var authority = Authority();
        using var otherAuthority = Authority();
        using var correct = ServerCertificate(authority);
        using var incorrect = ServerCertificate(otherAuthority);
        var pairing = Pair(authority);
        Assert.True(pairing.ValidateServerCertificate(Server, correct));
        Assert.False(pairing.ValidateServerCertificate(Server, incorrect));
        Assert.False(pairing.ValidateServerCertificate(new Uri("https://192.168.0.88:474/"), correct));
        Assert.False(pairing.ValidateServerCertificate(new Uri("https://192.168.0.87:5443/"), correct));
        Assert.False(pairing.ValidateServerCertificate(new Uri("http://192.168.0.87:474/"), correct));
        Assert.False(pairing.ValidateServerCertificate(new Uri("https://user@192.168.0.87:474/"), correct));
    }

    [Theory]
    [InlineData("wrong-host", false, false)]
    [InlineData("correct-host", true, false)]
    [InlineData("correct-host", false, true)]
    public void PairingDoesNotDisableHostnameExpiryOrPurposeChecks(string hostname, bool expired, bool clientOnly)
    {
        using var authority = Authority();
        using var certificate = ServerCertificate(authority, hostname == "correct-host", expired, clientOnly);
        Assert.False(Pair(authority).ValidateServerCertificate(Server, certificate));
    }

    [Fact]
    public void ServerCertificateCannotBeImportedAsAnAuthority()
    {
        using var authority = Authority();
        using var certificate = ServerCertificate(authority);
        Assert.Throws<ArgumentException>(() => NasPairing.Create("Family NAS", Server.AbsoluteUri, certificate.RawData));
    }

    [Fact]
    public void RejectsUnknownFieldsAndOversizedFiles()
    {
        Assert.Throws<JsonException>(() => NasPairing.Parse(Encoding.UTF8.GetBytes("{\"password\":\"not-a-pairing-file\"}")));
        Assert.Throws<ArgumentException>(() => NasPairing.Parse(new byte[NasPairing.MaximumFileBytes + 1]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task HttpTransportHonorsPairingBeforeSendingRequests(bool matchingAuthority)
    {
        using var authority = Authority();
        using var otherAuthority = Authority();
        using var generated = ServerCertificate(matchingAuthority ? authority : otherAuthority, loopback: true);
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = NasEndpoint.Parse($"https://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
        var pairing = NasPairing.Create("Test NAS", endpoint.BaseUri.AbsoluteUri, authority.RawData);
        var requestReceived = false;
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(cancellation.Token);
            await using var stream = new SslStream(socket.GetStream());
            try
            {
                await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, cancellation.Token);
                var buffer = new byte[1024];
                requestReceived = await stream.ReadAsync(buffer, cancellation.Token) > 0;
                if (requestReceived)
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), cancellation.Token);
            }
            catch (Exception exception) when (!matchingAuthority && exception is AuthenticationException or IOException) { }
        }, cancellation.Token);
        using var handler = pairing.CreateHttpHandler(endpoint);
        Assert.False(handler.AllowAutoRedirect);
        using var client = new HttpClient(handler);
        if (matchingAuthority)
        {
            var request = client.GetAsync(endpoint.BaseUri, cancellation.Token);
            await Task.WhenAll(server, request);
            using var response = await request;
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
        else
            await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync(endpoint.BaseUri, cancellation.Token));
        await server;
        Assert.Equal(matchingAuthority, requestReceived);
    }

    private static NasPairing Pair(X509Certificate2 authority) => NasPairing.Create("Family NAS", Server.AbsoluteUri, authority.RawData);

    private static X509Certificate2 Authority()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=TNAX Test CA", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddYears(-2), DateTimeOffset.UtcNow.AddYears(2));
    }

    private static X509Certificate2 ServerCertificate(X509Certificate2 authority, bool correctHost = true, bool expired = false, bool clientOnly = false, bool loopback = false)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=tnas", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new(clientOnly ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1")
        }, true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddIpAddress(IPAddress.Parse(loopback ? "127.0.0.1" : correctHost ? "192.168.0.87" : "192.168.0.88"));
        request.CertificateExtensions.Add(names.Build());
        using var certificate = request.Create(authority, DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow.AddDays(expired ? -1 : 30), RandomNumberGenerator.GetBytes(16));
        return certificate.CopyWithPrivateKey(key);
    }
}
