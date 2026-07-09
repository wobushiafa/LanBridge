using LanBridge.Common.Network;
using LanBridge.Common.Protocol;

namespace LanBridge.Tests.Integration;

/// <summary>
/// Wraps a real signaling transport (TCP <see cref="SignalingClient"/> or WS
/// <see cref="WebSocketSignalingClient"/>) for integration tests. Captures every
/// inbound message and exposes <see cref="WaitForAsync{T}"/> which fails loud on
/// timeout (never hangs) so wiring bugs surface as test failures.
/// </summary>
public sealed class TestClient : IAsyncDisposable
{
    private readonly SignalingTransportBase _transport;
    private readonly List<BaseMessage> _received = new();
    private readonly object _lock = new();
    private TaskCompletionSource<BaseMessage>? _waiter;

    public IReadOnlyList<BaseMessage> ReceivedSnapshot
    {
        get
        {
            lock (_lock) return _received.ToList();
        }
    }

    public bool IsConnected => _transport.IsConnected;

    private TestClient(SignalingTransportBase transport)
    {
        _transport = transport;
        _transport.OnMessageReceived += OnMessageAsync;
    }

    public static async Task<TestClient> ConnectTcpAsync(string host, int port)
    {
        var client = new SignalingClient(host, port);
        var wrapper = new TestClient(client);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return wrapper;
    }

    public static async Task<TestClient> ConnectWsAsync(string host, int port)
    {
        var client = new WebSocketSignalingClient(host, port);
        var wrapper = new TestClient(client);
        await client.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        return wrapper;
    }

    public Task SendAsync(BaseMessage msg)
    {
        var json = MessageSerializer.SerializeToString(msg);
        return _transport.SendAsync(json);
    }

    /// <summary>
    /// Waits until a message of type <typeparamref name="T"/> arrives, or throws
    /// <see cref="TimeoutException"/> after <paramref name="timeout"/>. Scans already-
    /// received messages first; re-arms if an unrelated message arrives.
    /// </summary>
    public async Task<T> WaitForAsync<T>(TimeSpan timeout) where T : BaseMessage
    {
        using var cts = new CancellationTokenSource(timeout);
        while (true)
        {
            TaskCompletionSource<BaseMessage> tcs;
            lock (_lock)
            {
                var existing = _received.OfType<T>().FirstOrDefault();
                if (existing != null)
                {
                    return existing;
                }
                tcs = new TaskCompletionSource<BaseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
                _waiter = tcs;
            }
            using var reg = cts.Token.Register(() => tcs.TrySetCanceled());
            BaseMessage msg;
            try
            {
                msg = await tcs.Task;
            }
            catch (OperationCanceledException)
            {
                var seen = ReceivedSnapshot.Select(m => m.GetType().Name).ToArray();
                throw new TimeoutException(
                    $"Timed out waiting for {typeof(T).Name} after {timeout.TotalSeconds:F1}s. " +
                    $"Received so far: [{string.Join(", ", seen)}]");
            }
            if (msg is T typed)
            {
                return typed;
            }
            // Unrelated message arrived; loop and re-arm (msg is already in _received).
        }
    }

    private Task OnMessageAsync(string json)
    {
        var msg = MessageSerializer.Deserialize(json);
        if (msg == null)
        {
            return Task.CompletedTask;
        }
        TaskCompletionSource<BaseMessage>? tcs;
        lock (_lock)
        {
            _received.Add(msg);
            tcs = _waiter;
            _waiter = null;
        }
        tcs?.TrySetResult(msg);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _transport.Dispose();
        return ValueTask.CompletedTask;
    }
}
