namespace LanBridge.Common.Network;

/// <summary>
/// Manages the signaling connection lifecycle with automatic reconnection.
/// Supports both TCP (SignalingClient) and WebSocket (WebSocketSignalingClient) transports.
/// </summary>
public sealed class SignalingConnectionLoop : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string>? _statusSink;
    private readonly Action? _onDisconnected;
    private readonly Func<string, Task> _onMessageAsync;
    private readonly Func<SignalingTransportBase?, Task> _onConnectedAsync;
    private readonly string _transportType; // "tcp", "ws", "auto"
    private readonly int _wsPort;
    private SignalingTransportBase? _transport;

    public bool IsConnected => _transport is SignalingClient tc ? tc.IsConnected :
                               _transport is WebSocketSignalingClient ws ? ws.IsConnected :
                               false;

    /// <summary>
    /// Legacy property: returns the TCP client if available.
    /// For new code, use Transport property instead.
    /// </summary>
    public SignalingClient? Client => _transport as SignalingClient;

    /// <summary>
    /// The current active transport (TCP or WebSocket).
    /// </summary>
    public SignalingTransportBase? Transport => _transport;

    public SignalingConnectionLoop(
        string host,
        int port,
        Action<string>? statusSink,
        Action? onDisconnected,
        Func<string, Task> onMessageAsync,
        Func<SignalingTransportBase?, Task> onConnectedAsync,
        string transportType = "tcp",
        int wsPort = 9010)
    {
        _host = host;
        _port = port;
        _statusSink = statusSink;
        _onDisconnected = onDisconnected;
        _onMessageAsync = onMessageAsync;
        _onConnectedAsync = onConnectedAsync;
        _transportType = transportType;
        _wsPort = wsPort;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (IsConnected)
            {
                try
                {
                    await Task.Delay(2000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                continue;
            }

            CleanupTransport();

            // Try transport based on configuration
            if (_transportType == "ws")
            {
                await TryConnectWebSocketAsync(cancellationToken);
            }
            else if (_transportType == "auto")
            {
                // Try TCP first, fallback to WS
                var tcpOk = await TryConnectTcpAsync(cancellationToken);
                if (!tcpOk)
                {
                    await TryConnectWebSocketAsync(cancellationToken);
                }
            }
            else
            {
                // Default: TCP only
                await TryConnectTcpAsync(cancellationToken);
            }
        }
    }

    private async Task<bool> TryConnectTcpAsync(CancellationToken cancellationToken)
    {
        try
        {
            _statusSink?.Invoke("Connecting to signaling server (TCP)...");
            var client = new SignalingClient(_host, _port);
            client.OnMessageReceived += message => _onMessageAsync(message);
            client.OnDisconnected += () =>
            {
                _onDisconnected?.Invoke();
                _statusSink?.Invoke("Disconnected from signaling server");
            };
            client.OnError += error => _statusSink?.Invoke($"Signaling error: {error}");

            _transport = client;
            await client.ConnectAsync();
            _statusSink?.Invoke("Connected to signaling server (TCP)");
            await _onConnectedAsync(client);
            return true;
        }
        catch (Exception ex)
        {
            _statusSink?.Invoke($"TCP connect failed: {ex.Message}");
            CleanupTransport();
            await WaitBeforeRetryAsync(cancellationToken);
            return false;
        }
    }

    private async Task<bool> TryConnectWebSocketAsync(CancellationToken cancellationToken)
    {
        try
        {
            _statusSink?.Invoke("Connecting to signaling server (WebSocket)...");
            var client = new WebSocketSignalingClient(_host, _wsPort);
            client.OnMessageReceived += message => _onMessageAsync(message);
            client.OnDisconnected += () =>
            {
                _onDisconnected?.Invoke();
                _statusSink?.Invoke("Disconnected from signaling server (WebSocket)");
            };
            client.OnError += error => _statusSink?.Invoke($"WebSocket signaling error: {error}");

            _transport = client;
            await client.ConnectAsync();
            _statusSink?.Invoke("Connected to signaling server (WebSocket)");
            await _onConnectedAsync(client);
            return true;
        }
        catch (Exception ex)
        {
            _statusSink?.Invoke($"WebSocket connect failed: {ex.Message}");
            CleanupTransport();
            await WaitBeforeRetryAsync(cancellationToken);
            return false;
        }
    }

    private async Task WaitBeforeRetryAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(5000, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CleanupTransport()
    {
        if (_transport == null)
        {
            return;
        }

        try
        {
            _transport.Dispose();
        }
        catch
        {
        }

        _transport = null;
    }

    public void Dispose()
    {
        CleanupTransport();
    }
}
