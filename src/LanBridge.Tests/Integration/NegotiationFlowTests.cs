using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests.Integration;

/// <summary>
/// REQ-6: Extranet sends <see cref="ConnectRequest"/> -> server routes
/// <see cref="HolePunchStart"/> to the intranet target and
/// <see cref="ConnectReady"/> back to the extranet requester (mediation routing,
/// no real UDP hole punch). Uses cross transports (intranet=WS, extranet=TCP) to
/// also exercise routing by clientId across the TCP/WS boundary.
/// REQ-7: Extranet sends <see cref="RelayRequest"/> -> both sides receive
/// <see cref="RelayAccept"/> with the correct role.
/// </summary>
public class NegotiationFlowTests
{
    [Fact(Timeout = 30000)]
    public async Task ConnectRequest_RoutesHolePunchStartAndConnectReady()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: true);
        await cluster.StartAsync();

        // Intranet over WS (so the relay/hole-punch target lives behind the WS bridge).
        await using var intranet = await TestClient.ConnectWsAsync(cluster.WsHost, cluster.WsPort);
        await intranet.SendAsync(new RegisterMessage
        {
            NodeId = "neg-intra",
            Token = "",
            PublicEndPoint = "127.0.0.1:5000"
        });
        var intraAck = await intranet.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));
        Assert.True(intraAck.Success);

        // Extranet over TCP.
        await using var extranet = await TestClient.ConnectTcpAsync(cluster.Host, cluster.TcpPort);
        await extranet.SendAsync(new RegisterMessage { NodeId = "neg-extra", Token = "" });
        var extraAck = await extranet.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));
        Assert.True(extraAck.Success);

        await extranet.SendAsync(new ConnectRequest
        {
            TargetNodeId = "neg-intra",
            ClientEndPoint = "127.0.0.1:6000"
        });

        var holePunchTask = intranet.WaitForAsync<HolePunchStart>(TimeSpan.FromSeconds(5));
        var connectReadyTask = extranet.WaitForAsync<ConnectReady>(TimeSpan.FromSeconds(5));
        await Task.WhenAll(holePunchTask, connectReadyTask);

        var holePunch = await holePunchTask;
        var connectReady = await connectReadyTask;
        Assert.False(string.IsNullOrEmpty(holePunch.SessionId));
        Assert.False(string.IsNullOrEmpty(connectReady.SessionId));
        Assert.Equal(holePunch.SessionId, connectReady.SessionId);
    }

    [Fact(Timeout = 30000)]
    public async Task RelayRequest_DeliversRelayAcceptToBothSides()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: true);
        await cluster.StartAsync();

        await using var intranet = await TestClient.ConnectWsAsync(cluster.WsHost, cluster.WsPort);
        await intranet.SendAsync(new RegisterMessage
        {
            NodeId = "relay-intra",
            Token = "",
            PublicEndPoint = "127.0.0.1:5001"
        });
        Assert.True((await intranet.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5))).Success);

        await using var extranet = await TestClient.ConnectTcpAsync(cluster.Host, cluster.TcpPort);
        await extranet.SendAsync(new RegisterMessage { NodeId = "relay-extra", Token = "" });
        Assert.True((await extranet.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5))).Success);

        await extranet.SendAsync(new RelayRequest { TargetNodeId = "relay-intra" });

        var extraAcceptTask = extranet.WaitForAsync<RelayAccept>(TimeSpan.FromSeconds(5));
        var intraAcceptTask = intranet.WaitForAsync<RelayAccept>(TimeSpan.FromSeconds(5));
        await Task.WhenAll(extraAcceptTask, intraAcceptTask);

        var extraAccept = await extraAcceptTask;
        var intraAccept = await intraAcceptTask;
        Assert.Equal("extranet", extraAccept.Role);
        Assert.Equal("intranet", intraAccept.Role);
        Assert.Equal(extraAccept.SessionId, intraAccept.SessionId);
        Assert.True(extraAccept.RelayPort > 0);
    }
}
