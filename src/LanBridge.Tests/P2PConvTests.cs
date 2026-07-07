using System.Net;
using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests;

public class P2PConvTests
{
    [Fact]
    public void FromEndpoints_IsCommutative()
    {
        var a = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 1234);
        var b = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 5678);
        Assert.Equal(P2PConv.FromEndpoints(a, b), P2PConv.FromEndpoints(b, a));
    }

    [Fact]
    public void FromEndpoints_DifferentPairs_DifferentConvs()
    {
        var a = new IPEndPoint(IPAddress.Parse("192.168.1.1"), 1234);
        var b = new IPEndPoint(IPAddress.Parse("192.168.1.2"), 5678);
        var c = new IPEndPoint(IPAddress.Parse("192.168.1.3"), 5678);
        Assert.NotEqual(P2PConv.FromEndpoints(a, b), P2PConv.FromEndpoints(a, c));
    }

    [Fact]
    public void FromEndpoints_ReturnsNonZero()
    {
        var a = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 1);
        var b = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 2);
        Assert.NotEqual(0u, P2PConv.FromEndpoints(a, b));
    }
}
