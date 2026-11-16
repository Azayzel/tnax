using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tnax.Core;

public sealed record NasPairing
{
    public const int MaximumFileBytes = 64 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true
    };

    public int Version { get; init; } = 1;
    public required string Name { get; init; }
    public required string ServerAddress { get; init; }
    public required string RootCertificate { get; init; }

    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(RootCertificate)));

    public static NasPairing Create(string name, string serverAddress, ReadOnlySpan<byte> certificateData)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(certificateData);
        var pairing = new NasPairing
        {
            Name = name.Trim(), ServerAddress = NasEndpoint.Parse(serverAddress).BaseUri.AbsoluteUri,
            RootCertificate = Convert.ToBase64String(certificate.RawData)
        };
        pairing.Validate();
        return pairing;
    }

    public static NasPairing Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length > MaximumFileBytes)
            throw new ArgumentException("This NAS pairing file is too large.");
        var pairing = JsonSerializer.Deserialize<NasPairing>(data, JsonOptions)
            ?? throw new ArgumentException("This is not a NAS pairing file.");
        pairing.Validate();
        return pairing;
    }

    public byte[] Export()
    {
        Validate();
        return JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions);
    }

    public void Validate()
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 80 || Name.Any(char.IsControl))
            throw new ArgumentException("This NAS pairing file has an unsupported format.");
        NasEndpoint.Parse(ServerAddress);
        if (string.IsNullOrWhiteSpace(RootCertificate) || RootCertificate.Length > 32 * 1024)
            throw new ArgumentException("This pairing file has an invalid CA certificate.");
        using var root = LoadRootCertificate();
        var constraints = root.Extensions.OfType<X509BasicConstraintsExtension>().FirstOrDefault();
        var usage = root.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
        if (constraints?.CertificateAuthority != true ||
            usage is not null && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            throw new ArgumentException("Choose the public root CA certificate, not the NAS server certificate or a private key.");
        if (DateTime.UtcNow < root.NotBefore.ToUniversalTime() || DateTime.UtcNow > root.NotAfter.ToUniversalTime())
            throw new ArgumentException("The pairing certificate is not currently valid. Ask the NAS owner for a new setup file.");
    }

    public X509Certificate2 LoadRootCertificate() => X509CertificateLoader.LoadCertificate(Convert.FromBase64String(RootCertificate));

    public bool Allows(Uri destination)
    {
        var server = NasEndpoint.Parse(ServerAddress).BaseUri;
        return destination.Scheme == server.Scheme && destination.Host == server.Host && destination.Port == server.Port &&
            string.IsNullOrEmpty(destination.UserInfo) && string.IsNullOrEmpty(destination.Query) && string.IsNullOrEmpty(destination.Fragment);
    }

    public bool ValidateServerCertificate(Uri destination, X509Certificate2? certificate, X509Chain? suppliedChain = null)
    {
        try
        {
            Validate();
            if (!Allows(destination) || certificate is null || !certificate.MatchesHostname(destination.Host, allowCommonName: false))
                return false;
            using var root = LoadRootCertificate();
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(root);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.DisableCertificateDownloads = true;
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));
            if (suppliedChain is not null)
                foreach (var element in suppliedChain.ChainElements.Cast<X509ChainElement>().Skip(1))
                    chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            return chain.Build(certificate);
        }
        catch (Exception exception) when (exception is CryptographicException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    public SocketsHttpHandler CreateHttpHandler(NasEndpoint endpoint)
    {
        Validate();
        if (!Allows(endpoint.BaseUri))
            throw new ArgumentException("This pairing belongs to a different NAS. Import its setup file or clear the pairing first.");
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = TimeSpan.FromSeconds(15),
            SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, chain, _) =>
                {
                    if (certificate is null)
                        return false;
                    using var remote = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    return ValidateServerCertificate(endpoint.BaseUri, remote, chain);
                }
            }
        };
    }
}
