using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Diagnostics;
using LanBridge.SignalingServer;

namespace LanBridge.Tests.Integration;

/// <summary>
/// In-process signaling cluster for integration tests. Spins up a real
/// <see cref="SignalingService"/> (TCP, OS-assigned ephemeral port) and an
/// optional <see cref="WebSocketSignalingService"/> (localhost-bound, no admin
/// required). No child processes. Use <c>await using var cluster = ...; await cluster.StartAsync();</c>
/// per test for port isolation.
/// </summary>
public sealed class SignalingTestCluster : IAsyncDisposable
{
    private readonly bool _wsEnabled;
    private SignalingService? _signaling;
    private WebSocketSignalingService? _wsSignaling;
    private CancellationTokenSource? _wsCts;
    private Task? _signalingTask;
    private Task? _wsTask;

    public int TcpPort { get; private set; }
    public int WsPort { get; private set; }
    public string Host => "127.0.0.1";

    /// <summary>
    /// Host WS clients must connect to. Always <c>localhost</c> (NOT <c>127.0.0.1</c>):
    /// the cluster starts <see cref="WebSocketSignalingService"/> with
    /// <c>bindAllNics:false</c>, which binds the <c>http://localhost:&lt;port&gt;/signaling/</c>
    /// HttpListener prefix. HttpListener matches the request's Host header against the
    /// registered prefix, so a client connecting to <c>ws://127.0.0.1:port/signaling</c>
    /// would be rejected (404/401). Production uses <c>bindAllNics:true</c> (<c>http://+:</c>)
    /// which matches any host, so this is a TEST-ONLY constraint.
    /// </summary>
    public string WsHost => "localhost";
    public SignalingService Signaling => _signaling ?? throw new InvalidOperationException("Cluster not started");

    public SignalingTestCluster(bool wsEnabled = false)
    {
        _wsEnabled = wsEnabled;
    }

    public async Task StartAsync()
    {
        // SignalingPort=0 -> OS picks an ephemeral port; we read ActualPort after Start.
        // We intentionally do NOT call ServerConfig.Validate() (it rejects port 0).
        var config = new ServerConfig { SignalingPort = 0 };
        var telemetry = new OperationalTelemetry();
        _signaling = new SignalingService(config, telemetry);

        _signalingTask = Task.Run(() => _signaling.StartAsync());

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (_signaling.ActualPort == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        TcpPort = _signaling.ActualPort;
        if (TcpPort == 0)
        {
            throw new InvalidOperationException("SignalingService did not bind a TCP port in time");
        }

        if (_wsEnabled)
        {
            WsPort = EphemeralPortHelper.AllocatePort();
            _wsSignaling = new WebSocketSignalingService(WsPort, _signaling, bindAllNics: false);
            _wsCts = new CancellationTokenSource();
            _wsTask = Task.Run(() => _wsSignaling.StartAsync(_wsCts.Token));
            await Task.Delay(100);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _wsCts?.Cancel();
        _wsSignaling?.Dispose();
        _signaling?.Dispose();

        try { if (_wsTask != null) await _wsTask; } catch { }
        try { if (_signalingTask != null) await _signalingTask; } catch { }
        _wsCts?.Dispose();
    }
}

internal static class EphemeralPortHelper
{
    /// <summary>
    /// Grabs an OS-assigned ephemeral port by binding a TcpListener on port 0,
    /// reading the assigned port, then closing. Acceptable race window in tests.
    /// Used for HttpListener (which does not support port 0).
    /// </summary>
    public static int AllocatePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
