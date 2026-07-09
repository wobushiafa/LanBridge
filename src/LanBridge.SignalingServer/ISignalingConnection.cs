using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

/// <summary>
/// Unified connection abstraction for a signaling transport (TCP, WebSocket, ...),
/// keyed by clientId in <see cref="SignalingService"/>. A concrete transport
/// implements <see cref="SendAsync"/> (transport framing) and
/// <see cref="DisconnectAsync"/> (transport-specific close). Adding a 3rd transport
/// requires no new branch in <see cref="SignalingService.SendToClientAsync"/> /
/// <see cref="SignalingService.DisconnectClientAsync"/> — only a new
/// <see cref="ISignalingConnection"/> implementation registered via
/// <see cref="SignalingService.RegisterConnection"/>.
/// </summary>
public interface ISignalingConnection : IAsyncDisposable
{
    /// <summary>
    /// Send a framed message to the peer. Framing is the implementer's concern:
    /// TCP prepends a 4-byte little-endian length prefix + UTF-8 JSON body; WebSocket
    /// sends a text frame with the same JSON body (no length prefix). The caller
    /// (<see cref="SignalingService.SendToClientAsync"/>) wraps this in a try/catch.
    /// </summary>
    Task SendAsync(BaseMessage message, CancellationToken ct);

    /// <summary>
    /// Close the transport so its receive loop exits and runs cleanup. Idempotent.
    /// For TCP: dispose the stream + socket + send lock. For WebSocket: close the WS.
    /// </summary>
    Task DisconnectAsync();
}
