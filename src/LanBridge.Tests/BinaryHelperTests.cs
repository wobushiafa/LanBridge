using LanBridge.Common.Kcp;
using Xunit;

namespace LanBridge.Tests;

public class BinaryHelperTests
{
    [Fact]
    public void WriteRead_UInt16_RoundTrips()
    {
        var buffer = new byte[2];
        BinaryHelper.WriteUInt16(buffer, 0x1234);
        Assert.Equal(0x1234, BinaryHelper.ReadUInt16(buffer));
    }

    [Fact]
    public void WriteRead_UInt32_RoundTrips()
    {
        var buffer = new byte[4];
        BinaryHelper.WriteUInt32(buffer, 0xDEADBEEF);
        Assert.Equal(0xDEADBEEFu, BinaryHelper.ReadUInt32(buffer));
    }

    [Fact]
    public void KcpSegment_EncodeDecode_RoundTrips()
    {
        var segment = new KcpSegment
        {
            Conv = 0x01020304,
            Cmd = KcpSegment.CmdPush,
            Frg = 1,
            Wnd = 100,
            Ts = 1234,
            Sn = 5678,
            Una = 9999,
            Len = 0
        };
        var buffer = new byte[KcpSegment.HeaderSize];
        segment.Encode(buffer);
        var decoded = KcpSegment.Decode(buffer);

        Assert.Equal(segment.Conv, decoded.Conv);
        Assert.Equal(segment.Cmd, decoded.Cmd);
        Assert.Equal(segment.Frg, decoded.Frg);
        Assert.Equal(segment.Wnd, decoded.Wnd);
        Assert.Equal(segment.Ts, decoded.Ts);
        Assert.Equal(segment.Sn, decoded.Sn);
        Assert.Equal(segment.Una, decoded.Una);
        Assert.Equal(segment.Len, decoded.Len);
    }
}
