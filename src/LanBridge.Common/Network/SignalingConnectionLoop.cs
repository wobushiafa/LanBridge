namespace LanBridge.Common.Network;

public sealed class SignalingConnectionLoop : IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly Action<string>? _statusSink;
    private readonly Action? _onDisconnected;
    private readonly Func<string, Task> _onMessageAsync;
    private readonly Func<SignalingClient, Task> _onConnectedAsync;
    private SignalingClient? _client;

    public bool IsConnected => _client?.IsConnected == true;
    public SignalingClient? Client => _client;

    public SignalingConnectionLoop(
        string host,
        int port,
        Action<string>? statusSink,
        Action? onDisconnected,
        Func<string, Task> onMessageAsync,
        Func<SignalingClient, Task> onConnectedAsync)
    {
        _host = host;
        _port = port;
        _statusSink = statusSink;
        _onDisconnected = onDisconnected;
        _onMessageAsync = onMessageAsync;
        _onConnectedAsync = onConnectedAsync;
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

            CleanupClient();

            try
            {
                _statusSink?.Invoke("Connecting to signaling server...");
                var client = new SignalingClient(_host, _port);
                client.OnMessageReceived += message => _ = _onMessageAsync(message);
                client.OnDisconnected += () =>
                {
                    _onDisconnected?.Invoke();
                    _statusSink?.Invoke("Disconnected from signaling server");
                };
                client.OnError += error => _statusSink?.Invoke($"Signaling error: {error}");

                _client = client;
                await client.ConnectAsync();
                _statusSink?.Invoke("Connected to signaling server");
                await _onConnectedAsync(client);
            }
            catch (Exception ex)
            {
                _statusSink?.Invoke($"Failed to connect to signaling server: {ex.Message}. Retrying in 5 seconds...");
                CleanupClient();

                try
                {
                    await Task.Delay(5000, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private void CleanupClient()
    {
        if (_client == null)
        {
            return;
        }

        try
        {
            _client.Dispose();
        }
        catch
        {
        }

        _client = null;
    }

    public void Dispose()
    {
        CleanupClient();
    }
}
