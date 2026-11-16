using Foundation;
using Security;
using System.Security.Cryptography.X509Certificates;
using Tnax.Core;

namespace Tnax.App;

internal sealed class IosPairedHandler : DelegatingHandler
{
    private readonly NasPairing pairing;
    private readonly X509Certificate2 root;

    public IosPairedHandler(NasPairing pairing, NSUrlSessionConfiguration configuration)
    {
        pairing.Validate();
        this.pairing = pairing;
        root = pairing.LoadRootCertificate();
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            DisableCertificateDownloads = true
        };
        policy.CustomTrustStore.Add(root);
        InnerHandler = new NSUrlSessionHandler(configuration) { AllowAutoRedirect = false, CertificateChainPolicy = policy };
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is null || !pairing.Allows(request.RequestUri))
            throw new InvalidOperationException("The request is outside the paired NAS.");
        return base.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
            root.Dispose();
    }
}

internal static class IosNasTrust
{
    public static void Handle(NasPairing? pairing, NSUrlAuthenticationChallenge challenge,
        Action<NSUrlSessionAuthChallengeDisposition, NSUrlCredential?> completion)
    {
        if (pairing is null || challenge.ProtectionSpace.AuthenticationMethod != "NSURLAuthenticationMethodServerTrust")
        {
            completion(NSUrlSessionAuthChallengeDisposition.PerformDefaultHandling, null);
            return;
        }
        try
        {
            pairing.Validate();
            var space = challenge.ProtectionSpace;
            var destination = new UriBuilder(Uri.UriSchemeHttps, space.Host, checked((int)space.Port)).Uri;
            var trust = space.ServerSecTrust;
            if (!pairing.Allows(destination) || trust is null)
            {
                completion(NSUrlSessionAuthChallengeDisposition.CancelAuthenticationChallenge, null);
                return;
            }
            using var root = pairing.LoadRootCertificate();
            using var policy = SecPolicy.CreateSslPolicy(true, destination.Host);
            trust.SetPolicy(policy);
            if (trust.SetAnchorCertificates(new X509Certificate2Collection(root)) != SecStatusCode.Success ||
                trust.SetAnchorCertificatesOnly(true) != SecStatusCode.Success)
            {
                completion(NSUrlSessionAuthChallengeDisposition.CancelAuthenticationChallenge, null);
                return;
            }
            trust.NetworkFetchAllowed = false;
            var trusted = trust.Evaluate(out var error);
            error?.Dispose();
            completion(trusted ? NSUrlSessionAuthChallengeDisposition.UseCredential : NSUrlSessionAuthChallengeDisposition.CancelAuthenticationChallenge,
                trusted ? NSUrlCredential.FromTrust(trust) : null);
        }
        catch (Exception)
        {
            completion(NSUrlSessionAuthChallengeDisposition.CancelAuthenticationChallenge, null);
        }
    }
}
