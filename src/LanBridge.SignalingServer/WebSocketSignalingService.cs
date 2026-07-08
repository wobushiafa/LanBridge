using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

/// <summary>
/// Optional WebSocket signaling listener on a separate HTTP port.
/// Accepts WebSocket upgrade requests and bridges messages to the
/// existing SignalingService message processing logic.
/// </summary>
public sealed class WebSocketSignalingService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly SignalingService _signalingService;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private bool _isRunning;

    public WebSocketSignalingService(int wsPort, SignalingService signalingService)
    {
        _signalingService = signalingService;
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://+:{wsPort}/signaling/");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _isRunning = true;
        _listener.Start();
        Console.WriteLine($"[WebSocket] Listening on port {_listener.Prefixes.First()}");

        while (_isRunning && !ct.IsCancellationRequested)
        {
            try
            {
                var context = await _listener.GetContextAsync();

                if (!context.Request.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                var wsContext = await context.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid().ToString("N")[..8];
                _clients[clientId] = wsContext.WebSocket;

                Console.WriteLine($"[WebSocket] Client connected: {clientId}");
                _ = Task.Run(() => HandleClientAsync(clientId, wsContext.WebSocket, ct), ct);
            }
            catch (Exception ex) when (!_isRunning)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WebSocket] Accept error: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(string clientId, WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var message = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    var baseMessage = MessageSerializer.Deserialize(message);
                    if (baseMessage != null)
                    {
                        // Process through the same logic as TCP signaling
                        await _signalingService.ProcessMessageFromTransportAsync(clientId, baseMessage);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            Console.WriteLine($"[WebSocket] Client {clientId} error: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Client {clientId} error: {ex.Message}");
        }
        finally
        {
            _clients.TryRemove(clientId, out _);

            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", CancellationToken.None);
                }
            }
            catch
            {
            }

            ws.Dispose();
            Console.WriteLine($"[WebSocket] Client disconnected: {clientId}");
        }
    }

    public async Task SendToClientAsync(string clientId, BaseMessage message)
    {
        if (!_clients.TryGetValue(clientId, out var ws) || ws.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            var json = MessageSerializer.SerializeToString(message);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WebSocket] Send error to {clientId}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _isRunning = false;

        foreach (var ws in _clients.Values)
        {
            try
            {
                if (ws.State == WebSocketState.Open)
                {
                    ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disposing", CancellationToken.None).GetAwaiter().GetResult();
                }
                ws.Dispose();
            }
            catch
            {
            }
        }

        _clients.Clear();

        try
        {
            _listener.Stop();
        }
        catch
        {
        }
    }
}
