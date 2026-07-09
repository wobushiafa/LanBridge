using System.Text;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Network;

namespace LanBridge.Common.Tui;

/// <summary>
/// Lightweight real-time TUI dashboard using ANSI escape codes.
/// Zero external dependencies, Native AOT compatible.
/// </summary>
public sealed class TuiDashboard : IDisposable
{
    private readonly Func<IReadOnlyList<NegotiatorStats>> _tunnelsStats;
    private readonly Func<IReadOnlyDictionary<string, long>> _telemetrySnapshot;
    private readonly DateTime _startTimeUtc;
    private readonly string _nodeName;
    private readonly string _role;
    private CancellationTokenSource? _cts;

    // Throughput tracking (computed in-render from snapshot deltas; single-threaded Render).
    private DateTime _lastSnapshotUtc = DateTime.UtcNow;
    private readonly Dictionary<string, (long Sent, long Recv)> _lastPerTunnel
        = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasBaseline;

    public TuiDashboard(
        string nodeName,
        string role,
        Func<IReadOnlyList<NegotiatorStats>> tunnelsStats,
        Func<IReadOnlyDictionary<string, long>> telemetrySnapshot)
    {
        _nodeName = nodeName;
        _role = role;
        _tunnelsStats = tunnelsStats;
        _telemetrySnapshot = telemetrySnapshot;
        _startTimeUtc = DateTime.UtcNow;
    }

    public void Run(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Hide cursor and enable alternate screen buffer
        Console.Write("\x1b[?25l\x1b[?1049h");

        try
        {
            while (!token.IsCancellationRequested)
            {
                Render();
                Thread.Sleep(1000);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            // Restore cursor and normal screen buffer
            Console.Write("\x1b[?25h\x1b[?1049l");
        }
    }

    private void Render()
    {
        var sb = new StringBuilder();
        var tunnels = _tunnelsStats();
        var telemetry = _telemetrySnapshot();
        var uptime = DateTime.UtcNow - _startTimeUtc;

        // Throughput: per-tunnel delta over elapsed time since last snapshot.
        var now = DateTime.UtcNow;
        var dt = (now - _lastSnapshotUtc).TotalSeconds;
        var dtValid = _hasBaseline && dt > 0;

        var rates = new Dictionary<string, (long Send, long Recv)>(StringComparer.OrdinalIgnoreCase);
        long totalSendRate = 0;
        long totalRecvRate = 0;
        foreach (var t in tunnels)
        {
            long sendRate = 0;
            long recvRate = 0;
            if (dtValid && _lastPerTunnel.TryGetValue(t.TargetNodeId, out var prev))
            {
                if (t.SentBytes >= prev.Sent)
                {
                    sendRate = (long)((t.SentBytes - prev.Sent) / dt);
                }
                if (t.ReceivedBytes >= prev.Recv)
                {
                    recvRate = (long)((t.ReceivedBytes - prev.Recv) / dt);
                }
            }
            rates[t.TargetNodeId] = (sendRate, recvRate);
            totalSendRate += sendRate;
            totalRecvRate += recvRate;
        }

        // Update baselines for next render.
        _lastPerTunnel.Clear();
        foreach (var t in tunnels)
        {
            _lastPerTunnel[t.TargetNodeId] = (t.SentBytes, t.ReceivedBytes);
        }
        _lastSnapshotUtc = now;
        _hasBaseline = true;

        // Move cursor to top-left and clear screen
        sb.Append("\x1b[H\x1b[2J");

        // Header
        sb.AppendLine($"\x1b[1;36m{'═', 50}\x1b[0m");
        sb.AppendLine($"\x1b[1;37m  LanBridge {_role} — {_nodeName}\x1b[0m");
        sb.AppendLine($"\x1b[1;36m{'═', 50}\x1b[0m");
        sb.AppendLine();

        // Summary line: use first connected tunnel, else first, else disconnected.
        var primary = tunnels.FirstOrDefault(t => t.Mode != PeerTransportMode.None) ??
                      (tunnels.Count > 0 ? tunnels[0] : null);

        var modeStr = primary?.Mode switch
        {
            PeerTransportMode.P2pDirect => "\x1b[32mP2P DIRECT\x1b[0m",
            PeerTransportMode.Relay => "\x1b[33mRELAY MODE\x1b[0m",
            _ => "\x1b[31mDISCONNECTED\x1b[0m"
        };
        var natType = primary?.NatType ?? "Unknown";
        var publicEp = primary?.PublicEndPoint ?? "n/a";
        var signaling = primary is { IsSignalingConnected: true }
            ? "\x1b[32mConnected\x1b[0m"
            : "\x1b[31mDisconnected\x1b[0m";

        sb.AppendLine($"  TRANSPORT: {modeStr}    NAT: {natType}");
        sb.AppendLine($"  Public: {publicEp}    Signaling: {signaling}");
        sb.AppendLine($"  Uptime: {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}" +
                      $"    Total: \x1b[36m↑ {FormatRate(totalSendRate)}\x1b[0m  \x1b[36m↓ {FormatRate(totalRecvRate)}\x1b[0m");
        sb.AppendLine();

        // Tunnels table
        sb.AppendLine($"\x1b[1;37m  Tunnels ({tunnels.Count}):\x1b[0m");
        if (tunnels.Count == 0)
        {
            sb.AppendLine("    \x1b[2m(no tunnels yet)\x1b[0m");
        }
        else
        {
            sb.AppendLine("    " + Pad("Target", 18) + Pad("Mode", 9) + Pad("RTT", 7) +
                          Pad("cwnd", 7) + Pad("↑rate", 11) + Pad("↓rate", 11) +
                          Pad("sent", 10) + Pad("recv", 10));
            foreach (var t in tunnels)
            {
                var mode = t.Mode switch
                {
                    PeerTransportMode.P2pDirect => "P2P",
                    PeerTransportMode.Relay => "RELAY",
                    _ => "DISC"
                };
                var rtt = t.RttMs < 0 ? "—" : $"{t.RttMs}ms";
                var cwnd = t.Mode == PeerTransportMode.None ? "—" : t.Cwnd.ToString();
                var (sendRate, recvRate) = rates.TryGetValue(t.TargetNodeId, out var r)
                    ? r
                    : ((long)0, (long)0);

                var rateLimitHint = t.RateLimitBytesPerSec > 0
                    ? $"  \x1b[33m{FormatBytes(t.RateLimitBytesPerSec)}/s util {t.TokenBucketUtilization * 100:F0}%\x1b[0m"
                    : "";

                sb.AppendLine("    " +
                    Pad(Truncate(t.TargetNodeId, 18), 18) +
                    Pad(mode, 9) +
                    Pad(rtt, 7) +
                    Pad(cwnd, 7) +
                    Pad(FormatRate(sendRate), 11) +
                    Pad(FormatRate(recvRate), 11) +
                    Pad(FormatBytes(t.SentBytes), 10) +
                    Pad(FormatBytes(t.ReceivedBytes), 10) +
                    rateLimitHint);
            }
        }
        sb.AppendLine();

        // Telemetry counters
        sb.AppendLine("\x1b[1;37m  Telemetry:\x1b[0m");
        if (telemetry.Count == 0)
        {
            sb.AppendLine("    \x1b[2m(no metrics)\x1b[0m");
        }
        else
        {
            foreach (var (key, value) in telemetry.OrderBy(kvp => kvp.Key).Take(10))
            {
                sb.AppendLine($"    {key}: {value}");
            }
        }
        sb.AppendLine();

        sb.Append("\x1b[2m  Press Ctrl+C to exit\x1b[0m");

        Console.Write(sb.ToString());
    }

    private static string FormatRate(long bytesPerSec)
    {
        if (bytesPerSec < 1024)
        {
            return $"{bytesPerSec} B/s";
        }
        if (bytesPerSec < 1024 * 1024)
        {
            return $"{bytesPerSec / 1024.0:F1} KB/s";
        }
        return $"{bytesPerSec / 1024.0 / 1024.0:F1} MB/s";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
        {
            return $"{bytes} B";
        }
        if (bytes < 1024 * 1024)
        {
            return $"{bytes / 1024.0:F1} KB";
        }
        if (bytes < 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }
        return $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB";
    }

    private static string Pad(string value, int width)
    {
        if (value.Length >= width)
        {
            return value;
        }
        return value + new string(' ', width - value.Length);
    }

    private static string Truncate(string value, int width)
    {
        return value.Length <= width ? value : value[..width];
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
