using System.Collections.Concurrent;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

/// <summary>
/// Replaces SignalingMessageRouter for multi-tunnel scenarios.
/// Routes signaling messages to the correct ConnectionNegotiator handler
/// based on sessionId (for ConnectReady/HolePunchStart/RelayAccept)
/// or nodeId (for RegisterAck/Error).
/// </summary>
public sealed class SignalingMessageDispatcher
{
    private readonly Action<string>? _statusSink;

    // sessionId → handler for session-scoped messages
    private readonly ConcurrentDictionary<string, ISignalingHandler> _sessionHandlers = new(StringComparer.OrdinalIgnoreCase);
    // nodeId → handler for node-scoped messages (RegisterAck, etc.)
    private readonly ConcurrentDictionary<string, ISignalingHandler> _nodeHandlers = new(StringComparer.OrdinalIgnoreCase);
    // Fallback handler for messages that can't be routed (e.g. uncorrelated errors)
    private ISignalingHandler? _fallbackHandler;

    public SignalingMessageDispatcher(Action<string>? statusSink)
    {
        _statusSink = statusSink;
    }

    public void RegisterSession(string sessionId, ISignalingHandler handler)
    {
        _sessionHandlers[sessionId] = handler;
    }

    public void UnregisterSession(string sessionId)
    {
        _sessionHandlers.TryRemove(sessionId, out _);
    }

    public void RegisterNode(string nodeId, ISignalingHandler handler)
    {
        _nodeHandlers[nodeId] = handler;
    }

    public void UnregisterNode(string nodeId)
    {
        _nodeHandlers.TryRemove(nodeId, out _);
    }

    public void SetFallbackHandler(ISignalingHandler handler)
    {
        _fallbackHandler = handler;
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
                // Broadcast to all node handlers — they all care about their own registration
                foreach (var handler in _nodeHandlers.Values)
                {
                    await handler.HandleRegisterAckAsync(ack);
                }
                break;

            case MessageType.ConnectReady:
                var ready = (ConnectReady)baseMessage;
                if (!string.IsNullOrWhiteSpace(ready.SessionId) &&
                    _sessionHandlers.TryGetValue(ready.SessionId, out var readyHandler))
                {
                    await readyHandler.HandleConnectReadyAsync(ready);
                }
                else if (_fallbackHandler != null)
                {
                    await _fallbackHandler.HandleConnectReadyAsync(ready);
                }
                break;

            case MessageType.HolePunchStart:
                var punch = (HolePunchStart)baseMessage;
                if (!string.IsNullOrWhiteSpace(punch.SessionId) &&
                    _sessionHandlers.TryGetValue(punch.SessionId, out var punchHandler))
                {
                    await punchHandler.HandleHolePunchStartAsync(punch);
                }
                else if (_fallbackHandler != null)
                {
                    await _fallbackHandler.HandleHolePunchStartAsync(punch);
                }
                break;

            case MessageType.RelayAccept:
                var relay = (RelayAccept)baseMessage;
                if (!string.IsNullOrWhiteSpace(relay.SessionId) &&
                    _sessionHandlers.TryGetValue(relay.SessionId, out var relayHandler))
                {
                    await relayHandler.HandleRelayAcceptAsync(relay);
                }
                else if (_fallbackHandler != null)
                {
                    await _fallbackHandler.HandleRelayAcceptAsync(relay);
                }
                break;

            case MessageType.Error:
                var error = (ErrorMessage)baseMessage;
                // Try to route by nodeId if the error mentions one
                var errorRouted = false;
                foreach (var handler in _nodeHandlers.Values)
                {
                    await handler.HandleErrorAsync(error);
                    errorRouted = true;
                }
                if (!errorRouted && _fallbackHandler != null)
                {
                    await _fallbackHandler.HandleErrorAsync(error);
                }
                break;
        }
    }
}

/// <summary>
/// Interface for signaling message handlers.
/// Each ConnectionNegotiator implements this to receive its own messages.
/// </summary>
public interface ISignalingHandler
{
    Task HandleRegisterAckAsync(RegisterAck ack);
    Task HandleConnectReadyAsync(ConnectReady ready);
    Task HandleHolePunchStartAsync(HolePunchStart punch);
    Task HandleRelayAcceptAsync(RelayAccept relay);
    Task HandleErrorAsync(ErrorMessage error);
}
