namespace LanBridge.Common.Network;

/// <summary>
/// Shared signaling infrastructure for multi-tunnel scenarios.
/// Owns the SignalingConnectionLoop and SignalingMessageDispatcher,
/// allowing multiple ConnectionNegotiator instances to share one
/// TCP connection to the signaling server.
/// </summary>
public sealed class SharedSignalingStack : IDisposable
{
    private readonly SignalingConnectionLoop _connectionLoop;
    private readonly SignalingMessageDispatcher _dispatcher;

    public SignalingConnectionLoop ConnectionLoop => _connectionLoop;
    public SignalingMessageDispatcher Dispatcher => _dispatcher;
    public bool IsConnected => _connectionLoop.IsConnected;
    public SignalingClient? Client => _connectionLoop.Client;

    public SharedSignalingStack(
        string host,
        int port,
        Action<string>? statusSink,
        string transportType = "tcp",
        int wsPort = 9010)
    {
        _dispatcher = new SignalingMessageDispatcher(statusSink);

        _connectionLoop = new SignalingConnectionLoop(
            host,
            port,
            statusSink,
            onDisconnected: () => { },
            onMessageAsync: message => _dispatcher.DispatchAsync(message),
            onConnectedAsync: _ => Task.CompletedTask,
            transportType,
            wsPort);
    }

    public void Dispose()
    {
        _connectionLoop.Dispose();
    }
}
