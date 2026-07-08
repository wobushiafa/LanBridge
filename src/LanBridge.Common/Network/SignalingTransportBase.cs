namespace LanBridge.Common.Network;

/// <summary>
/// Abstract base for signaling transports (TCP and WebSocket).
/// Provides shared send locking, cancellation, and event infrastructure.
/// </summary>
public abstract class SignalingTransportBase : IDisposable
{
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    protected CancellationTokenSource _cts = new();
    private volatile bool _isConnected;

    public abstract bool IsConnected { get; }
    public event Func<string, Task>? OnMessageReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;

    /// <summary>
    /// Subclass implements the actual send logic.
    /// </summary>
    protected abstract Task SendCoreAsync(string message, CancellationToken ct);

    /// <summary>
    /// Subclass calls this when a message is received.
    /// </summary>
    protected Task HandleMessageAsync(string message)
    {
        var handler = OnMessageReceived;
        return handler != null ? handler(message) : Task.CompletedTask;
    }

    /// <summary>
    /// Subclass calls this when disconnected.
    /// </summary>
    protected void HandleDisconnected()
    {
        _isConnected = false;
        OnDisconnected?.Invoke();
    }

    /// <summary>
    /// Subclass calls this on error.
    /// </summary>
    protected void HandleError(string error)
    {
        OnError?.Invoke(error);
    }

    protected void SetConnected(bool connected) => _isConnected = connected;

    /// <summary>
    /// Thread-safe send with lock protection.
    /// </summary>
    public async Task SendAsync(string message)
    {
        if (!_isConnected)
        {
            throw new InvalidOperationException("Not connected");
        }

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            await SendCoreAsync(message, _cts.Token);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Dispose()
    {
        _isConnected = false;
        _cts.Cancel();
        _cts.Dispose();
        _sendLock.Dispose();
        DisposeCore();
    }

    protected abstract void DisposeCore();
}
