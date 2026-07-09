using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using LanBridge.Common.Protocol;
using LanBridge.Common.Runtime;

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
        ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Listening on {_listener.Prefixes.First()}", ConsoleColor.Gray);

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
                var ws = wsContext.WebSocket;
                _clients[clientId] = ws;

                _signalingService.RegisterTransportSender(
                    clientId,
                    (msg, _) => SendToClientAsync(clientId, msg),
                    () => ws.State == WebSocketState.Open
                        ? ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server disconnect", CancellationToken.None)
                        : Task.CompletedTask);

                ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Client connected: {clientId}", ConsoleColor.Gray);
                _ = Task.Run(() => HandleClientAsync(clientId, ws, ct), ct);
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Accept error: {ex.Message}", ConsoleColor.Red);
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
            ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Client {clientId} error: {ex.Message}", ConsoleColor.Red);
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Client {clientId} error: {ex.Message}", ConsoleColor.Red);
        }
        finally
        {
            _signalingService.OnTransportClientDisconnected(clientId);
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
            ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Client disconnected: {clientId}", ConsoleColor.Gray);
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
            ConsoleStatusWriter.WriteServerStatus("WebSocket", $"Send error to {clientId}: {ex.Message}", ConsoleColor.Red);
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
