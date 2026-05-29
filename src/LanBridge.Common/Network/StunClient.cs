using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public static class StunClient
{
    public static async Task<StunBindingResult> QueryAsync(
        UdpHolePuncher socket,
        IPEndPoint serverEndPoint,
        int timeoutMs = 3000,
        bool changePort = false)
    {
        var transactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray();
        var request = StunProtocol.CreateBindingRequest(changePort: changePort, transactionId: transactionId);

        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        socket.RegisterStunRequest(transactionId, tcs);

        try
        {
            socket.StartReceivePump();
            await socket.SendAsync(request, request.Length, serverEndPoint);

            using var cts = new CancellationTokenSource(timeoutMs);
            using (cts.Token.Register(() => tcs.TrySetCanceled()))
            {
                var responseBuffer = await tcs.Task;
                if (StunProtocol.TryParseBindingSuccessResponse(responseBuffer, transactionId, out var mappedEndPoint) &&
                    mappedEndPoint != null)
                {
                    return new StunBindingResult(mappedEndPoint, serverEndPoint, StandardProtocol: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException($"STUN query to {serverEndPoint} timed out after {timeoutMs}ms");
        }
        finally
        {
            socket.UnregisterStunRequest(transactionId);
        }

        throw new Exception($"Failed to parse STUN response from {serverEndPoint}");
    }

    public static async Task<StunBindingResult> QueryAsync(
        UdpHolePuncher socket,
        string host,
        int port,
        int timeoutMs = 3000,
        bool changePort = false)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses[0];
        return await QueryAsync(socket, new IPEndPoint(ip, port), timeoutMs, changePort);
    }

    public static async Task<IPEndPoint?> QueryPublicEndPointV6Async(
        UdpHolePuncher socket,
        string host,
        int port,
        int timeoutMs = 2000)
    {
        var serverEndPoint = await ResolveServerAsync(host, port, AddressFamily.InterNetworkV6);
        if (serverEndPoint == null)
        {
            return null;
        }

        try
        {
            var result = await QueryAsync(socket, serverEndPoint, timeoutMs);
            return result.PublicEndPoint;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<NatDetectionResult> DetectNatAsync(
        UdpHolePuncher socket,
        string host,
        int primaryPort,
        int alternatePort,
        int timeoutMs = 3000)
    {
        StunBindingResult primary;
        try
        {
            primary = await QueryAsync(socket, host, primaryPort, timeoutMs);
        }
        catch (Exception ex)
        {
            return new NatDetectionResult(StunNatType.Blocked, $"No STUN binding response: {ex.Message}", null, null, false);
        }

        var localPort = socket.LocalEndPoint?.Port ?? 0;
        var portPreserved = localPort > 0 && localPort == primary.PublicEndPoint.Port;

        if (alternatePort <= 0 || alternatePort == primaryPort)
        {
            return new NatDetectionResult(
                StunNatType.Unknown,
                "Only one STUN endpoint is configured; NAT type cannot be fully classified",
                primary.PublicEndPoint,
                null,
                portPreserved);
        }

        StunBindingResult? alternate = null;
        try
        {
            alternate = await QueryAsync(socket, host, alternatePort, timeoutMs);
        }
        catch
        {
            return new NatDetectionResult(
                StunNatType.Unknown,
                "Alternate STUN endpoint did not respond; cannot distinguish symmetric NAT from filtering behavior",
                primary.PublicEndPoint,
                null,
                portPreserved);
        }

        if (!Equals(primary.PublicEndPoint, alternate.PublicEndPoint))
        {
            return new NatDetectionResult(
                StunNatType.Symmetric,
                "Public mapping changed when probing a different STUN endpoint; direct P2P is likely to fail and relay should be used",
                primary.PublicEndPoint,
                alternate.PublicEndPoint,
                portPreserved);
        }

        try
        {
            await QueryAsync(socket, host, primaryPort, timeoutMs, changePort: true);
            return new NatDetectionResult(
                StunNatType.RestrictedCone,
                "Mapping is stable and packets from another server port can return; P2P is likely possible",
                primary.PublicEndPoint,
                alternate.PublicEndPoint,
                portPreserved);
        }
        catch
        {
            return new NatDetectionResult(
                StunNatType.PortRestrictedCone,
                "Mapping is stable but changed-port response was filtered; simultaneous hole punching may work, otherwise relay is expected",
                primary.PublicEndPoint,
                alternate.PublicEndPoint,
                portPreserved);
        }
    }

    private static async Task<IPEndPoint?> ResolveServerAsync(string host, int port, AddressFamily family)
    {
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            var address = addresses.FirstOrDefault(a => a.AddressFamily == family);
            return address != null ? new IPEndPoint(address, port) : null;
        }
        catch
        {
            return null;
        }
    }
}
