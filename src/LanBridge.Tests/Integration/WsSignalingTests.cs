using LanBridge.Common.Network;
using LanBridge.Common.Protocol;
using Xunit;

namespace LanBridge.Tests.Integration;

/// <summary>
/// REQ-3: WebSocket signaling register roundtrip — the path that was previously
/// broken (server outbound bridge to WS clients). Verifies RegisterAck actually
/// arrives at a WS client.
/// REQ-4: <c>transportType=auto</c> falls back from TCP to WS when TCP is
/// unreachable, completing registration via WS within the timeout.
/// </summary>
public class WsSignalingTests
{
    [Fact(Timeout = 30000)]
    public async Task Ws_Register_ReturnsSuccessAck()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: true);
        await cluster.StartAsync();

        await using var client = await TestClient.ConnectWsAsync(cluster.WsHost, cluster.WsPort);
        await client.SendAsync(new RegisterMessage { NodeId = "ws-node-1", Token = "" });

        var ack = await client.WaitForAsync<RegisterAck>(TimeSpan.FromSeconds(5));

        Assert.True(ack.Success, $"Expected Success=true, got: {ack.Message}");
    }

    [Fact(Timeout = 30000)]
    public async Task Auto_FallsBackToWs_WhenTcpUnreachable()
    {
        await using var cluster = new SignalingTestCluster(wsEnabled: true);
        await cluster.StartAsync();

        // A closed TCP port so the TCP connect in 'auto' fails fast (connection refused),
        // forcing fallback to WS. (cluster.TcpPort IS listening, so we deliberately point
        // the loop at a different, closed port.)
        var closedTcpPort = EphemeralPortHelper.AllocatePort();

        var ackReady = new TaskCompletionSource<RegisterAck>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task OnMessage(string json)
        {
            if (MessageSerializer.Deserialize(json) is RegisterAck ack)
            {
                ackReady.TrySetResult(ack);
            }
            return Task.CompletedTask;
        }

        Task OnConnected(SignalingTransportBase? transport)
        {
            if (transport != null)
            {
                var register = new RegisterMessage { NodeId = "auto-node", Token = "" };
                return transport.SendAsync(MessageSerializer.SerializeToString(register));
            }
            return Task.CompletedTask;
        }

        using var cts = new CancellationTokenSource();
        var loop = new SignalingConnectionLoop(
            cluster.WsHost, closedTcpPort,
            statusSink: null, onDisconnected: null,
            onMessageAsync: OnMessage,
            onConnectedAsync: OnConnected,
            transportType: "auto",
            wsPort: cluster.WsPort);

        var runTask = Task.Run(() => loop.RunAsync(cts.Token));

        var ack = await ackReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(ack.Success);

        cts.Cancel();
        loop.Dispose();
        try { await runTask; } catch { }
    }
}
