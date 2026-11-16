namespace Tnax.Core;

public sealed class NasEndpoint
{
    public Uri BaseUri { get; }

    private NasEndpoint(Uri baseUri) => BaseUri = baseUri;

    public static NasEndpoint Parse(string address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            throw new ArgumentException("Enter the full HTTPS WebDAV folder address.");

        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("The address must not contain credentials, a query, or a fragment.");

        return new NasEndpoint(new Uri(uri.AbsoluteUri.TrimEnd('/') + "/"));
    }

    public Uri Resource(params string[] segments)
    {
        if (segments.Length == 0)
            return BaseUri;

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment) || segment is "." or ".." ||
                segment.IndexOfAny(['/', '\\', '%']) >= 0 || segment.Any(char.IsControl))
                throw new ArgumentException("A destination segment contains an unsafe path character.");
        }

        return new Uri(BaseUri, string.Join('/', segments.Select(Uri.EscapeDataString)));
    }

    public bool Contains(Uri resource) =>
        resource.Scheme == BaseUri.Scheme && resource.Host == BaseUri.Host &&
        resource.Port == BaseUri.Port && string.IsNullOrEmpty(resource.UserInfo) &&
        string.IsNullOrEmpty(resource.Query) && string.IsNullOrEmpty(resource.Fragment) &&
        resource.AbsolutePath.StartsWith(BaseUri.AbsolutePath, StringComparison.Ordinal);
}
