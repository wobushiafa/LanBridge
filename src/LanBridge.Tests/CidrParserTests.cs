using System.Net;
using LanBridge.Common.Configuration;
using Xunit;

namespace LanBridge.Tests;

public class CidrParserTests
{
    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24", null)]
    [InlineData("192.168.1.0/24:80", "192.168.1.0/24", 80)]
    [InlineData("192.168.1.0/24:*", "192.168.1.0/24", null)]
    public void TryParse_Valid_ReturnsExpected(string input, string expectedCidr, int? expectedPort)
    {
        Assert.True(CidrParser.TryParse(input, out var subnet));
        Assert.Equal(expectedCidr, subnet.Cidr);
        Assert.Equal(expectedPort, subnet.Port);
    }

    [Theory]
    [InlineData("192.168.1.0")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1.0/-1")]
    [InlineData("192.168.1.0/24:abc")]
    public void TryParse_Invalid_ReturnsFalse(string input)
    {
        Assert.False(CidrParser.TryParse(input, out _));
    }

    [Theory]
    [InlineData("192.168.1.10", "192.168.1.0/24", true)]
    [InlineData("192.168.2.10", "192.168.1.0/24", false)]
    [InlineData("10.0.0.1", "192.168.1.0/24", false)]
    [InlineData("192.168.1.1", "192.168.1.1/32", true)]
    [InlineData("192.168.1.1", "0.0.0.0/0", true)]
    public void IsInCidr_Ipv4_ReturnsExpected(string address, string cidr, bool expected)
    {
        Assert.Equal(expected, CidrParser.IsInCidr(IPAddress.Parse(address), cidr));
    }

    [Theory]
    [InlineData("fe80::1", "fe80::/64", true)]
    [InlineData("fe80::1", "fe80:1::/64", false)]
    [InlineData("2001:db8::1", "fe80::/64", false)]
    public void IsInCidr_Ipv6_ReturnsExpected(string address, string cidr, bool expected)
    {
        Assert.Equal(expected, CidrParser.IsInCidr(IPAddress.Parse(address), cidr));
    }

    [Fact]
    public void AddSubnets_ParsesMultiple()
    {
        var subnets = new List<AllowedSubnet>();
        CidrParser.AddSubnets("192.168.1.0/24,10.0.0.0/8", subnets);
        Assert.Equal(2, subnets.Count);
    }
}
