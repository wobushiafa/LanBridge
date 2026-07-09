using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Network;

namespace LanBridge.Tests.Integration;

/// <summary>
/// Builds two <see cref="KcpSession"/>s bound to loopback ephemeral UDP ports, each
/// pointing at the other's local endpoint, both owning their UDP receive loop.
/// Lets integration tests exercise the real KCP data path (Send → KCP → UDP → peer
/// receive → InputPacket → OnDataReceived) without running a real hole-punch.
/// </summary>
/// <remarks>
/// <see cref="KcpSession.Dispose"/> does NOT dispose the underlying
/// <see cref="UdpClient"/> it was constructed with, so this pair holds the udp
/// client references and disposes them itself (after the sessions) in
/// <see cref="Dispose"/>. If the udp clients were leaked, subsequent tests would
/// hold bound ports until GC.
/// </remarks>
public sealed class KcpLoopbackPair : IDisposable
{
    private readonly UdpClient _udpA;
    private readonly UdpClient _udpB;
    private bool _disposed;

    public KcpSession SessionA { get; }
    public KcpSession SessionB { get; }

    public KcpLoopbackPair(uint? conv = null, bool verbose = false)
    {
        var convVal = conv ?? (uint)Random.Shared.Next(1, int.MaxValue);

        _udpA = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _udpB = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var epA = (IPEndPoint)_udpA.Client.LocalEndPoint!;
        var epB = (IPEndPoint)_udpB.Client.LocalEndPoint!;

        SessionA = new KcpSession(convVal, _udpA, epB, ownReceiveLoop: true);
        SessionB = new KcpSession(convVal, _udpB, epA, ownReceiveLoop: true);

        if (verbose)
        {
            SessionA.OnTrace += m => { };
            SessionB.OnTrace += m => { };
        }
    }

    /// <summary>
    /// Starts both KCP update + receive loops. Use this when the sessions are used
    /// directly (REQ-1/REQ-4). When the sessions are attached to a
    /// <see cref="PeerTransportSession"/> via <see cref="PeerTransportSession.UseP2p"/>,
    /// do NOT call this — <see cref="PeerTransportSession.UseP2p"/> starts the session
    /// itself and a double-start would spin a second receive loop on the same socket.
    /// </summary>
    public void Start()
    {
        SessionA.Start();
        SessionB.Start();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;

        // Sessions first: they stop referencing the udp clients via their receive
        // loops, then we close the sockets underneath.
        //
        // A session attached to a PeerTransportSession is co-owned: PeerTransportSession.Dispose
        // (via DisposeCurrent) disposes the KcpSession first, so by the time the pair is disposed
        // the session is already gone. KcpSession.Dispose is NOT idempotent (_cts.Cancel throws
        // ObjectDisposedException), so swallow that case here. KcpSession.Dispose when the session
        // was used standalone (REQ-1/REQ-4, no PeerTransportSession) still runs the first-time path.
        TryDisposeSession(SessionA);
        TryDisposeSession(SessionB);
        _udpA.Dispose();
        _udpB.Dispose();
    }

    private static void TryDisposeSession(KcpSession session)
    {
        try
        {
            session.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // Already disposed by the owning PeerTransportSession — expected co-ownership case.
        }
    }
}
