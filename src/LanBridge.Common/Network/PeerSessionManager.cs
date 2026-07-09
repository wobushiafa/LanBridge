using System.Collections.Concurrent;
using LanBridge.Common.Diagnostics;

namespace LanBridge.Common.Network;

public sealed class PeerSessionManager : IDisposable
{
    private readonly PeerConnectionOptions _options;
    private readonly ConcurrentDictionary<string, PeerTransportSession> _sessions = new();
    private string _activeSessionId = "default";

    public event Action<byte[], int>? OnDataReceived;
    public event Action<string, byte[], int>? OnSessionDataReceived;
    public event Action<PeerTransportMode>? OnModeChanged;
    public event Action<string>? OnStatusChanged;
    public event Action<string, string>? OnP2pUnhealthy;

    public PeerSessionManager(PeerConnectionOptions options)
    {
        _options = options;
    }

    public string ActiveSessionId => _activeSessionId;

    public int SessionCount => _sessions.Count;

    public IEnumerable<string> SessionIds => _sessions.Keys;

    public PeerTransportMode Mode => GetSession(_activeSessionId).Mode;

    public bool IsConnected => GetSession(_activeSessionId).IsConnected;

    public PeerTransportSession GetSession(string sessionId)
    {
        return _sessions.GetOrAdd(sessionId, id =>
        {
            var transport = new PeerTransportSession(_options.Verbose);
            transport.SetPriority(_options.Priority);
            if (_options.RateLimitBytesPerSec > 0)
            {
                transport.SetRateLimit(new TokenBucket(_options.RateLimitBytesPerSec));
            }
            transport.OnDataReceived += (data, length) =>
            {
                OnDataReceived?.Invoke(data, length);
                OnSessionDataReceived?.Invoke(id, data, length);
            };
            transport.OnDisconnected += () =>
            {
                if (id == _activeSessionId)
                {
                    OnModeChanged?.Invoke(PeerTransportMode.None);
                }
                OnStatusChanged?.Invoke($"Transport disconnected: session {id}");
            };
            transport.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
            transport.OnP2pUnhealthy += reason => OnP2pUnhealthy?.Invoke(id, reason);
            return transport;
        });
    }

    public void SetActiveSession(string sessionId)
    {
        _activeSessionId = sessionId;
    }

    public async Task SendAsync(byte[] data, int offset, int length)
    {
        await SendAsync(_activeSessionId, data, offset, length);
    }

    public async Task SendAsync(string sessionId, byte[] data, int offset, int length)
    {
        await GetSession(sessionId).SendAsync(data, offset, length);
    }

    public Task SendHighPriorityAsync(string sessionId, byte[] data, int offset, int length)
    {
        return GetSession(sessionId).SendHighPriorityAsync(data, offset, length);
    }

    public Task SendHighPriorityAsync(byte[] data, int offset, int length)
    {
        return SendHighPriorityAsync(_activeSessionId, data, offset, length);
    }

    public PeerSessionStats GetStatsSnapshot()
    {
        return GetSession(_activeSessionId).GetStatsSnapshot();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }
        _sessions.Clear();
    }
}
