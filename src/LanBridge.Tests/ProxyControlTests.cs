using System.Text;
using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests;

public class ProxyControlTests
{
    [Fact]
    public void IsCloseTarget_ExactBytes_ReturnsTrue()
    {
        var bytes = Encoding.UTF8.GetBytes(ProxyControl.CloseTarget);
        Assert.True(ProxyControl.IsCloseTarget(bytes, bytes.Length));
    }

    [Fact]
    public void IsCloseTarget_WrongLength_ReturnsFalse()
    {
        var bytes = Encoding.UTF8.GetBytes(ProxyControl.CloseTarget + "x");
        Assert.False(ProxyControl.IsCloseTarget(bytes, bytes.Length));
    }

    [Fact]
    public void IsCloseTarget_DifferentContent_ReturnsFalse()
    {
        var bytes = Encoding.UTF8.GetBytes("P2P_CONTROL:OTHER");
        Assert.False(ProxyControl.IsCloseTarget(bytes, bytes.Length));
    }
}
