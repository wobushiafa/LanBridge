using System.Net.WebSockets;
using System.Text;

namespace LanBridge.Common.Network;

/// <summary>
/// WebSocket-based signaling transport.
/// Uses text frames with JSON content (same as TCP, without 4-byte length prefix).
/// </summary>
public sealed class WebSocketSignalingClient : SignalingTransportBase
{
    private ClientWebSocket? _ws;
    private readonly string _serverUrl;

    public override bool IsConnected => _ws?.State == WebSocketState.Open;

    public WebSocketSignalingClient(string host, int port, bool useTls = false)
    {
        var scheme = useTls ? "wss" : "ws";
        _serverUrl = $"{scheme}://{host}:{port}/signaling";
    }

    public async Task ConnectAsync()
    {
        try
        {
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(_serverUrl), CancellationToken.None);
            SetConnected(true);
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            HandleError($"WebSocket connect error: {ex.Message}");
            throw;
        }
    }

    protected override async Task SendCoreAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open)
        {
            throw new InvalidOperationException("WebSocket not connected");
        }

        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];

        try
        {
            while (_ws?.State == WebSocketState.Open)
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    HandleDisconnected();
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleMessageAsync(message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            HandleError($"WebSocket receive error: {ex.Message}");
            HandleDisconnected();
        }
        catch (Exception ex)
        {
            HandleError($"WebSocket error: {ex.Message}");
            HandleDisconnected();
        }
    }

    protected override void DisposeCore()
    {
        try
        {
            if (_ws?.State == WebSocketState.Open)
            {
                _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).GetAwaiter().GetResult();
            }
        }
        catch
        {
        }

        _ws?.Dispose();
    }
}
