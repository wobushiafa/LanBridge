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
    private readonly Func<NegotiatorStats> _negotiatorStats;
    private readonly Func<IReadOnlyDictionary<string, long>> _telemetrySnapshot;
    private readonly DateTime _startTimeUtc;
    private readonly string _nodeName;
    private readonly string _role;
    private CancellationTokenSource? _cts;

    public TuiDashboard(
        string nodeName,
        string role,
        Func<NegotiatorStats> negotiatorStats,
        Func<IReadOnlyDictionary<string, long>> telemetrySnapshot)
    {
        _nodeName = nodeName;
        _role = role;
        _negotiatorStats = negotiatorStats;
        _telemetrySnapshot = telemetrySnapshot;
        _startTimeUtc = DateTime.UtcNow;
    }

    public void Run(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;

        // Hide cursor and enable alternate screen buffer
        Console.Write("[?25l[?1049h");

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
            Console.Write("[?25h[?1049l");
        }
    }

    private void Render()
    {
        var sb = new StringBuilder();
        var negotiator = _negotiatorStats();
        var telemetry = _telemetrySnapshot();
        var uptime = DateTime.UtcNow - _startTimeUtc;

        // Move cursor to top-left and clear screen
        sb.Append("[H[2J");

        // Header
        sb.AppendLine($"[1;36m{'═',50}[0m");
        sb.AppendLine($"[1;37m  LanBridge {_role} — {_nodeName}[0m");
        sb.AppendLine($"[1;36m{'═',50}[0m");

        // Connection Status
        var modeStr = negotiator.Mode switch
        {
            PeerTransportMode.P2pDirect => "[32mP2P DIRECT[0m",
            PeerTransportMode.Relay => "[33mRELAY MODE[0m",
            _ => "[31mDISCONNECTED[0m"
        };

        sb.AppendLine($"  TRANSPORT: {modeStr}    NAT: {negotiator.NatType}");
        sb.AppendLine($"  Public: {negotiator.PublicEndPoint ?? "n/a"}    Signaling: {(negotiator.IsSignalingConnected ? "[32mConnected[0m" : "[31mDisconnected[0m")}");
        sb.AppendLine($"  Target: {negotiator.TargetNodeId}    Sessions: {negotiator.ActiveSessionCount}");
        sb.AppendLine();

        // Telemetry counters
        sb.AppendLine("[1;37m  Telemetry:[0m");
        foreach (var (key, value) in telemetry.OrderBy(kvp => kvp.Key).Take(8))
        {
            sb.AppendLine($"    {key}: {value}");
        }
        sb.AppendLine();

        // Uptime
        sb.AppendLine($"  Uptime: {uptime.Hours:D2}:{uptime.Minutes:D2}:{uptime.Seconds:D2}");
        sb.AppendLine();
        sb.Append($"[2m  Press Ctrl+C to exit[0m");

        Console.Write(sb.ToString());
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
