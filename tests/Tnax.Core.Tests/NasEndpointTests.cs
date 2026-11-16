using Tnax.Core;

namespace Tnax.Core.Tests;

public sealed class NasEndpointTests
{
    [Theory]
    [InlineData("http://nas.example/Photos")]
    [InlineData("ftp://nas.example/Photos")]
    [InlineData("https://user:password@nas.example/Photos")]
    [InlineData("https://nas.example/Photos?token=secret")]
    [InlineData("https://nas.example/Photos#fragment")]
    [InlineData("not an address")]
    public void RejectsUnsafeEndpoints(string address) =>
        Assert.Throws<ArgumentException>(() => NasEndpoint.Parse(address));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("../other")]
    [InlineData("folder\\other")]
    [InlineData("%2e%2e")]
    [InlineData("name\n.jpg")]
    [InlineData("")]
    public void RejectsUnsafeSegments(string segment) =>
        Assert.Throws<ArgumentException>(() => NasEndpoint.Parse("https://nas.example/Photos").Resource(segment));

    [Fact]
    public void EncodesNamesAndKeepsFolderBoundary()
    {
        var endpoint = NasEndpoint.Parse("https://nas.example/Photos");
        var resource = endpoint.Resource("phone", "holiday #1.heic");
        Assert.Equal("https://nas.example/Photos/phone/holiday%20%231.heic", resource.AbsoluteUri);
        Assert.True(endpoint.Contains(resource));
        Assert.False(endpoint.Contains(new Uri("https://nas.example/Photos-other/file.jpg")));
        Assert.False(endpoint.Contains(new Uri("https://other.example/Photos/file.jpg")));
        Assert.False(endpoint.Contains(new Uri("https://nas.example:8443/Photos/file.jpg")));
    }
}
