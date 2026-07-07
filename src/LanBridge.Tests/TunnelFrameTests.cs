using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests;

public class TunnelFrameTests
{
    [Fact]
    public void EncodeDecode_DataFrame_RoundTrips()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = TunnelFrame.Data(42, payload, 0, payload.Length);
        var encoded = frame.Encode();

        Assert.True(TunnelFrame.TryDecode(encoded, encoded.Length, out var decoded));
        Assert.Equal(TunnelFrameType.Data, decoded.Type);
        Assert.Equal(42u, decoded.StreamId);
        Assert.True(decoded.Payload.Span.SequenceEqual(payload));
    }

    [Fact]
    public void EncodeDecode_UnreliableDataFrame_RoundTrips()
    {
        var payload = new byte[] { 9, 8, 7 };
        var frame = TunnelFrame.UnreliableData(7, payload, 0, payload.Length);
        var encoded = frame.Encode();

        Assert.True(TunnelFrame.TryDecode(encoded, encoded.Length, out var decoded));
        Assert.Equal(TunnelFrameType.UnreliableData, decoded.Type);
        Assert.Equal(7u, decoded.StreamId);
        Assert.True(decoded.Payload.Span.SequenceEqual(payload));
    }

    [Fact]
    public void TryDecode_BufferTooShort_ReturnsFalse()
    {
        var buffer = new byte[TunnelFrame.HeaderSize - 1];
        Assert.False(TunnelFrame.TryDecode(buffer, buffer.Length, out _));
    }

    [Fact]
    public void TryDecode_WrongMagic_ReturnsFalse()
    {
        var buffer = new byte[TunnelFrame.HeaderSize];
        buffer[0] = 0xFF;
        Assert.False(TunnelFrame.TryDecode(buffer, buffer.Length, out _));
    }

    [Fact]
    public void TryDecode_LengthMismatch_ReturnsFalse()
    {
        var payload = new byte[] { 1, 2, 3 };
        var frame = TunnelFrame.Data(1, payload, 0, payload.Length);
        var encoded = frame.Encode();
        Assert.False(TunnelFrame.TryDecode(encoded, encoded.Length - 1, out _));
    }

    [Fact]
    public void WriteHeader_EncodesExpectedFields()
    {
        var buffer = new byte[TunnelFrame.HeaderSize + 4];
        TunnelFrame.WriteHeader(buffer, 0, TunnelFrameType.Open, 99, 4);

        Assert.True(TunnelFrame.TryDecode(buffer, buffer.Length, out var decoded));
        Assert.Equal(TunnelFrameType.Open, decoded.Type);
        Assert.Equal(99u, decoded.StreamId);
        Assert.Equal(4, decoded.Payload.Length);
    }

    [Fact]
    public void PayloadAsString_DecodesUtf8()
    {
        var text = "hello";
        var frame = TunnelFrame.Error(1, text);
        Assert.Equal(text, frame.PayloadAsString());
    }
}
