using LanBridge.Common.Configuration;
using Xunit;

namespace LanBridge.Tests;

public class EndpointParserTests
{
    [Theory]
    [InlineData("host:80", "host", 80, "tcp")]
    [InlineData("host:53:udp", "host", 53, "udp")]
    [InlineData("192.168.1.1:554:tcp", "192.168.1.1", 554, "tcp")]
    public void TryParseHostPortProtocol_Valid_ReturnsTrue(string input, string expectedHost, int expectedPort, string expectedProtocol)
    {
        Assert.True(EndpointParser.TryParseHostPortProtocol(input, out var host, out var port, out var protocol));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
        Assert.Equal(expectedProtocol, protocol);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("host:")]
    [InlineData(":80")]
    [InlineData("host:abc")]
    [InlineData("host:70000")]
    public void TryParseHostPortProtocol_Invalid_ReturnsFalse(string input)
    {
        Assert.False(EndpointParser.TryParseHostPortProtocol(input, out _, out _, out _));
    }

    [Theory]
    [InlineData("80=192.168.1.1:554:tcp", 80, "192.168.1.1", 554, "tcp")]
    [InlineData("53=8.8.8.8:53:udp", 53, "8.8.8.8", 53, "udp")]
    [InlineData("8080=127.0.0.1:80", 8080, "127.0.0.1", 80, "tcp")]
    public void TryParseTunnelMapping_Valid_ReturnsTrue(string input, int expectedLocalPort, string expectedHost, int expectedPort, string expectedProtocol)
    {
        Assert.True(EndpointParser.TryParseTunnelMapping(input, out var mapping));
        Assert.Equal(expectedLocalPort, mapping.LocalPort);
        Assert.Equal(expectedHost, mapping.TargetHost);
        Assert.Equal(expectedPort, mapping.TargetPort);
        Assert.Equal(expectedProtocol, mapping.Protocol);
    }

    [Theory]
    [InlineData("abc=host:80")]
    [InlineData("80=")]
    [InlineData("80host:80")]
    [InlineData("80=host:abc")]
    public void TryParseTunnelMapping_Invalid_ReturnsFalse(string input)
    {
        Assert.False(EndpointParser.TryParseTunnelMapping(input, out _));
    }

    [Theory]
    [InlineData("host", "host", null)]
    [InlineData("host:80", "host", 80)]
    [InlineData("host:*", "host", null)]
    [InlineData("host:any", "host", null)]
    public void TryParseOptionalPort_Valid_ReturnsExpected(string input, string expectedHost, int? expectedPort)
    {
        Assert.True(EndpointParser.TryParseOptionalPort(input, out var host, out var port));
        Assert.Equal(expectedHost, host);
        Assert.Equal(expectedPort, port);
    }

    [Theory]
    [InlineData("host1,host2:80", 2)]
    [InlineData("host1:80,host2:90,", 2)]
    public void AddTargets_ParsesMultiple(string input, int expectedCount)
    {
        var targets = new List<TargetEndpoint>();
        EndpointParser.AddTargets(input, targets);
        Assert.Equal(expectedCount, targets.Count);
    }
}
