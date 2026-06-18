using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public sealed class SignalingMessageRouter
{
    private readonly Action<string>? _statusSink;
    private readonly Func<ConnectReady, Task> _onConnectReadyAsync;
    private readonly Func<HolePunchStart, Task> _onHolePunchStartAsync;
    private readonly Func<RelayAccept, Task> _onRelayAcceptAsync;
    private readonly Func<ErrorMessage, Task> _onErrorAsync;

    public SignalingMessageRouter(
        Action<string>? statusSink,
        Func<ConnectReady, Task> onConnectReadyAsync,
        Func<HolePunchStart, Task> onHolePunchStartAsync,
        Func<RelayAccept, Task> onRelayAcceptAsync,
        Func<ErrorMessage, Task> onErrorAsync)
    {
        _statusSink = statusSink;
        _onConnectReadyAsync = onConnectReadyAsync;
        _onHolePunchStartAsync = onHolePunchStartAsync;
        _onRelayAcceptAsync = onRelayAcceptAsync;
        _onErrorAsync = onErrorAsync;
    }

    public async Task DispatchAsync(string message)
    {
        var baseMessage = MessageSerializer.Deserialize(message);
        if (baseMessage == null)
        {
            return;
        }

        switch (baseMessage.Type)
        {
            case MessageType.RegisterAck:
                var ack = (RegisterAck)baseMessage;
                _statusSink?.Invoke($"Registration {(ack.Success ? "success" : "failed")}: {ack.Message}");
                break;

            case MessageType.ConnectReady:
                await _onConnectReadyAsync((ConnectReady)baseMessage);
                break;

            case MessageType.HolePunchStart:
                await _onHolePunchStartAsync((HolePunchStart)baseMessage);
                break;

            case MessageType.RelayAccept:
                await _onRelayAcceptAsync((RelayAccept)baseMessage);
                break;

            case MessageType.Error:
                await _onErrorAsync((ErrorMessage)baseMessage);
                break;
        }
    }
}
