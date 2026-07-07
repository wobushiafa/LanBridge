using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests;

public class MessageSerializerTests
{
    [Fact]
    public void RegisterMessage_RoundTrips()
    {
        var msg = new RegisterMessage
        {
            NodeId = "node-1",
            Token = "token",
            NatType = StunNatType.Symmetric
        };
        var serialized = MessageSerializer.SerializeToString(msg);
        var deserialized = MessageSerializer.Deserialize(serialized) as RegisterMessage;
        Assert.NotNull(deserialized);
        Assert.Equal("node-1", deserialized.NodeId);
        Assert.Equal("token", deserialized.Token);
        Assert.Equal(StunNatType.Symmetric, deserialized.NatType);
    }

    [Fact]
    public void ConnectRequest_RoundTrips()
    {
        var msg = new ConnectRequest
        {
            TargetNodeId = "target",
            ClientEndPoint = "1.2.3.4:1234",
            NatType = StunNatType.PortRestrictedCone
        };
        var serialized = MessageSerializer.SerializeToString(msg);
        var deserialized = MessageSerializer.Deserialize(serialized) as ConnectRequest;
        Assert.NotNull(deserialized);
        Assert.Equal("target", deserialized.TargetNodeId);
        Assert.Equal("1.2.3.4:1234", deserialized.ClientEndPoint);
    }

    [Fact]
    public void ConnectReady_RoundTrips()
    {
        var msg = new ConnectReady
        {
            SessionId = "abc",
            IntranetEndPoint = "10.0.0.1:1000",
            ExtranetEndPoint = "20.0.0.1:2000",
            RelayAvailable = true,
            Conv = 123u
        };
        var serialized = MessageSerializer.SerializeToString(msg);
        var deserialized = MessageSerializer.Deserialize(serialized) as ConnectReady;
        Assert.NotNull(deserialized);
        Assert.Equal("abc", deserialized.SessionId);
        Assert.True(deserialized.RelayAvailable);
        Assert.Equal(123u, deserialized.Conv);
    }

    [Fact]
    public void Deserialize_UnknownType_ReturnsNull()
    {
        var json = "{\"type\":99,\"timestamp\":1}";
        Assert.Null(MessageSerializer.Deserialize(json));
    }

    [Fact]
    public void Deserialize_MissingType_ReturnsNull()
    {
        var json = "{\"timestamp\":1}";
        Assert.Null(MessageSerializer.Deserialize(json));
    }
}
