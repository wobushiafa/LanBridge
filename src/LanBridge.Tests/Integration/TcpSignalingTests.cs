using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests.Integration;

/// <summary>
/// REQ-2: TCP signaling register roundtrip (client -> server -> client).
/// REQ-5: TCP + WS clients coexist on one server, routed by clientId without cross-talk.
/// Catches "outbound routing broken" bugs (server never sends the ack back).
/// </summary>
public class TcpSignalingTests
{
    [Fact(Timeout = 30000)]
    public async Task Tcp_Register_ReturnsSuccessAck()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: false);
        await cluster.StartAsync();

        await using var client = await TestClient.ConnectTcpAsync(cluster.Host, cluster.TcpPort);
        await client.SendAsync(new RegisterMessage { NodeId = "tcp-node-1", Token = "" });

        var ack = await client.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));

        Assert.True(ack.Success, $"Expected Success=true, got: {ack.Message}");
    }

    [Fact(Timeout = 30000)]
    public async Task TcpAndWs_Coexist_BothReceiveOwnAck()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: true);
        await cluster.StartAsync();

        await using var tcpClient = await TestClient.ConnectTcpAsync(cluster.Host, cluster.TcpPort);
        await using var wsClient = await TestClient.ConnectWsAsync(cluster.WsHost, cluster.WsPort);

        await tcpClient.SendAsync(new RegisterMessage { NodeId = "coex-tcp", Token = "" });
        await wsClient.SendAsync(new RegisterMessage { NodeId = "coex-ws", Token = "" });

        var tcpAck = await tcpClient.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));
        var wsAck = await wsClient.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));

        Assert.True(tcpAck.Success);
        Assert.True(wsAck.Success);
        // Each transport should only have received its own RegisterAck (routed by clientId).
        Assert.Single(tcpClient.ReceivedSnapshot);
        Assert.Single(wsClient.ReceivedSnapshot);
    }
}
