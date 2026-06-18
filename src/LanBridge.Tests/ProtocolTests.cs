using System.Net;
using LanBridge.Common.Configuration;
using LanBridge.Common.Network;
using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests;

public class ProtocolTests
{
    [Fact]
    public void TargetDescriptorParser_ParsesUdpTarget()
    {
        var parsed = TargetDescriptorParser.TryParse("192.168.1.10:554:udp", out var descriptor);

        Assert.True(parsed);
        Assert.Equal("192.168.1.10", descriptor.Host);
        Assert.Equal(554, descriptor.Port);
        Assert.Equal("udp", descriptor.Protocol);
    }

    [Fact]
    public void TargetDescriptorParser_RejectsInvalidTarget()
    {
        Assert.False(TargetDescriptorParser.TryParse("bad-target", out _));
    }

    [Fact]
    public void TunnelFrame_RoundTripsUnreliablePayload()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var frame = TunnelFrame.UnreliableData(42, payload, 0, payload.Length);
        var encoded = frame.Encode();

        var decoded = TunnelFrame.TryDecode(encoded, encoded.Length, out var result);

        Assert.True(decoded);
        Assert.Equal(TunnelFrameType.UnreliableData, result.Type);
        Assert.Equal((uint)42, result.StreamId);
        Assert.True(result.Payload.Span.SequenceEqual(payload));
    }

    [Fact]
    public void MessageSerializer_RoundTripsRegisterMessage()
    {
        var message = new RegisterMessage
        {
            NodeId = "node-a",
            Token = "secret",
            PublicEndPoint = "203.0.113.10:4500",
            NatType = StunNatType.Symmetric
        };

        var json = MessageSerializer.SerializeToString(message);
        var decoded = MessageSerializer.Deserialize<RegisterMessage>(json);

        Assert.NotNull(decoded);
        Assert.Equal(message.NodeId, decoded!.NodeId);
        Assert.Equal(message.Token, decoded.Token);
        Assert.Equal(message.PublicEndPoint, decoded.PublicEndPoint);
        Assert.Equal(StunNatType.Symmetric, decoded.NatType);
    }

    [Fact]
    public void StunProtocol_RoundTripsBindingRequestAndResponse()
    {
        var transactionId = new byte[] { 1, 3, 3, 7, 9, 9, 2, 4, 6, 8, 1, 2 };
        var request = StunProtocol.CreateBindingRequest(changePort: true, transactionId: transactionId);
        var endPoint = new IPEndPoint(IPAddress.Parse("203.0.113.10"), 45000);

        var parsedRequest = StunProtocol.TryParseBindingRequest(request, out var bindingRequest);
        var response = StunProtocol.CreateBindingSuccessResponse(transactionId, endPoint);
        var parsedResponse = StunProtocol.TryParseBindingSuccessResponse(response, transactionId, out var decodedEndPoint);

        Assert.True(parsedRequest);
        Assert.True(bindingRequest.ChangePort);
        Assert.True(parsedResponse);
        Assert.Equal(endPoint, decodedEndPoint);
    }

    [Fact]
    public void CidrMatcher_MatchesExpectedRange()
    {
        Assert.True(CidrMatcher.IsInCidr(IPAddress.Parse("192.168.1.88"), "192.168.1.0/24"));
        Assert.False(CidrMatcher.IsInCidr(IPAddress.Parse("10.0.0.88"), "192.168.1.0/24"));
    }

    [Fact]
    public void P2pFailureExplainer_UsesSymmetricNatMessage()
    {
        var detection = new NatDetectionResult(
            StunNatType.Symmetric,
            "symmetric nat detected",
            null,
            null,
            false);

        var explanation = P2pFailureExplainer.Describe(detection);

        Assert.Contains("Symmetric NAT", explanation);
    }
}
