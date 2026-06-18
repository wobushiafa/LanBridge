using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Runtime;

namespace LanBridge.SignalingServer;

public class RelayService : IDisposable
{
    private readonly TcpListener _listener;
    private readonly int _maxSessions;
    private readonly TimeSpan _sessionTimeout;
    private readonly OperationalTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, RelaySession> _sessions = new();
    private readonly CancellationTokenSource _cleanupCts = new();
    private bool _isRunning;

    public RelayService(int port, int maxSessions, int relayTimeoutMs, OperationalTelemetry telemetry)
    {
        _listener = new TcpListener(IPAddress.Any, port);
        _maxSessions = maxSessions;
        _sessionTimeout = TimeSpan.FromMilliseconds(relayTimeoutMs);
        _telemetry = telemetry;
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;
        _ = Task.Run(() => CleanupIdleSessionsAsync(_cleanupCts.Token), _cleanupCts.Token);
        ConsoleStatusWriter.WriteServerStatus("Relay", $"Listening on TCP port {((_listener.LocalEndpoint as IPEndPoint)?.Port ?? 0)}", ConsoleColor.Gray);

        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = Task.Run(() => HandleRelayClientAsync(client));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleStatusWriter.WriteServerStatus("Relay", $"Accept error: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private async Task HandleRelayClientAsync(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            var buffer = new byte[65536];
            var lengthBuffer = new byte[4];
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                var read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                if (read == 0)
                {
                    return;
                }

                bytesRead += read;
            }

            var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            bytesRead = 0;
            while (bytesRead < messageLength)
            {
                var read = await stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                if (read == 0)
                {
                    return;
                }

                bytesRead += read;
            }

            var handshake = Encoding.UTF8.GetString(buffer, 0, messageLength);
            var parts = handshake.Split('|', 2);
            var sessionId = parts[0];
            var role = parts.Length > 1 ? parts[1] : string.Empty;

            if (!_sessions.TryGetValue(sessionId, out var session))
            {
                if (_sessions.Count >= _maxSessions)
                {
                    _telemetry.Increment("relay_sessions_rejected");
                    ConsoleStatusWriter.WriteServerStatus("Relay", $"Rejecting session {sessionId}: max sessions reached", ConsoleColor.Red);
                    client.Dispose();
                    return;
                }

                session = new RelaySession
                {
                    SessionId = sessionId,
                    CreatedAt = DateTime.UtcNow,
                    LastActivityUtc = DateTime.UtcNow,
                    IsActive = true
                };
                _sessions[sessionId] = session;
            }

            session.LastActivityUtc = DateTime.UtcNow;

            if (string.Equals(role, "intranet", StringComparison.OrdinalIgnoreCase))
            {
                session.IntranetClient?.Dispose();
                session.IntranetClient = client;
                session.IntranetClientId = sessionId;
                ConsoleStatusWriter.WriteServerStatus("Relay", $"Intranet client connected to session {sessionId}", ConsoleColor.Gray);
            }
            else if (string.Equals(role, "extranet", StringComparison.OrdinalIgnoreCase))
            {
                session.ExtranetClient?.Dispose();
                session.ExtranetClient = client;
                session.ExtranetClientId = sessionId;
                ConsoleStatusWriter.WriteServerStatus("Relay", $"Extranet client connected to session {sessionId}", ConsoleColor.Gray);
            }
            else if (session.IntranetClient == null)
            {
                session.IntranetClient = client;
                session.IntranetClientId = sessionId;
                ConsoleStatusWriter.WriteServerStatus("Relay", $"Client connected as intranet fallback to session {sessionId}", ConsoleColor.Gray);
            }
            else if (session.ExtranetClient == null)
            {
                session.ExtranetClient = client;
                session.ExtranetClientId = sessionId;
                ConsoleStatusWriter.WriteServerStatus("Relay", $"Client connected as extranet fallback to session {sessionId}", ConsoleColor.Gray);
            }

            if (session.IntranetClient != null && session.ExtranetClient != null)
            {
                _telemetry.Increment("relay_sessions_started");
                await StartRelayAsync(session);
            }
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("Relay", $"Client error: {ex.Message}", ConsoleColor.Red);
        }
    }

    private async Task StartRelayAsync(RelaySession session)
    {
        if (session.IntranetClient == null || session.ExtranetClient == null)
        {
            return;
        }

        var intranetStream = session.IntranetClient.GetStream();
        var extranetStream = session.ExtranetClient.GetStream();
        var task1 = RelayStreamAsync(intranetStream, extranetStream, session);
        var task2 = RelayStreamAsync(extranetStream, intranetStream, session);
        await Task.WhenAny(task1, task2);
        TerminateSession(session.SessionId, "ended", "relay_sessions_ended");
    }

    private async Task RelayStreamAsync(NetworkStream source, NetworkStream destination, RelaySession session)
    {
        var buffer = new byte[65536];
        try
        {
            while (session.IsActive)
            {
                var bytesRead = await source.ReadAsync(buffer, 0, buffer.Length);
                if (bytesRead == 0)
                {
                    break;
                }

                session.LastActivityUtc = DateTime.UtcNow;
                await destination.WriteAsync(buffer, 0, bytesRead);
                await destination.FlushAsync();
            }
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("Relay", $"Stream error on session {session.SessionId}: {ex.Message}", ConsoleColor.Red);
        }
    }

    private async Task CleanupIdleSessionsAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var expiredSessions = _sessions.Values
                .Where(session => session.IsActive && DateTime.UtcNow - session.LastActivityUtc > _sessionTimeout)
                .Select(session => session.SessionId)
                .ToArray();

            foreach (var sessionId in expiredSessions)
            {
                TerminateSession(sessionId, "timed out", "relay_sessions_timed_out");
            }
        }
    }

    private void TerminateSession(string sessionId, string reason, string telemetryCounter)
    {
        if (!_sessions.TryRemove(sessionId, out var session))
        {
            return;
        }

        session.IsActive = false;
        try { session.IntranetClient?.Dispose(); } catch { }
        try { session.ExtranetClient?.Dispose(); } catch { }
        _telemetry.Increment(telemetryCounter);
        ConsoleStatusWriter.WriteServerStatus("Relay", $"Session {sessionId} {reason}", ConsoleColor.DarkYellow);
    }

    public void Dispose()
    {
        _isRunning = false;
        _cleanupCts.Cancel();
        _cleanupCts.Dispose();
        _listener.Stop();

        foreach (var sessionId in _sessions.Keys.ToArray())
        {
            TerminateSession(sessionId, "disposed", "relay_sessions_disposed");
        }
    }
}
