using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

/// <summary>
/// Delegate-backed <see cref="ISignalingConnection"/> for non-TCP transports
/// (WebSocket). Wraps the two hooks (send + disconnect) a transport listener
/// supplies when registering via <see cref="SignalingService.RegisterConnection"/>.
/// Equivalent to the former <c>TransportBridge</c> record, now a connection object.
/// </summary>
public sealed class BridgeSignalingConnection : ISignalingConnection
{
    private readonly Func<BaseMessage, CancellationToken, Task> _send;
    private readonly Func<Task> _disconnect;

    public BridgeSignalingConnection(
        Func<BaseMessage, CancellationToken, Task> send,
        Func<Task>? disconnectAsync = null)
    {
        _send = send;
        _disconnect = disconnectAsync ?? (() => Task.CompletedTask);
    }

    public Task SendAsync(BaseMessage message, CancellationToken ct) => _send(message, ct);

    public Task DisconnectAsync() => _disconnect();

    public ValueTask DisposeAsync() => new(DisconnectAsync());
}
