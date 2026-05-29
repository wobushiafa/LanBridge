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
        string host,
        int port,
        int timeoutMs = 3000,
        bool changePort = false)
    {
        var serverEndPoint = await ResolveServerAsync(host, port);
        var transactionId = Guid.NewGuid().ToByteArray().AsSpan(0, 12).ToArray();
        var request = StunProtocol.CreateBindingRequest(changePort: changePort, transactionId: transactionId);
        await socket.SendAsync(request, request.Length, serverEndPoint);

        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(timeout.Token);
                if (StunProtocol.TryParseBindingSuccessResponse(result.Buffer, transactionId, out var mappedEndPoint) &&
                    mappedEndPoint != null)
                {
                    return new StunBindingResult(mappedEndPoint, result.RemoteEndPoint, StandardProtocol: true);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (changePort)
        {
            throw new TimeoutException("standard STUN change-port test timeout");
        }

        return await QueryLegacyAsync(socket, serverEndPoint, timeoutMs);
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

    private static async Task<StunBindingResult> QueryLegacyAsync(UdpHolePuncher socket, IPEndPoint serverEndPoint, int timeoutMs)
    {
        var request = Encoding.UTF8.GetBytes("STUN_REQUEST");
        await socket.SendAsync(request, request.Length, serverEndPoint);

        using var timeout = new CancellationTokenSource(timeoutMs);
        while (!timeout.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(timeout.Token);
                var response = Encoding.UTF8.GetString(result.Buffer);
                var endpoint = TryParseLegacyResponse(response);
                if (endpoint != null)
                {
                    return new StunBindingResult(endpoint, result.RemoteEndPoint, StandardProtocol: false);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        throw new TimeoutException("STUN request timeout - no response received");
    }

    private static IPEndPoint? TryParseLegacyResponse(string response)
    {
        var stunResponse = MessageSerializer.Deserialize<StunResponse>(response);
        if (stunResponse != null &&
            !string.IsNullOrWhiteSpace(stunResponse.PublicIp) &&
            stunResponse.PublicPort > 0)
        {
            return new IPEndPoint(IPAddress.Parse(stunResponse.PublicIp), stunResponse.PublicPort);
        }

        using var doc = JsonDocument.Parse(response);
        var root = doc.RootElement;
        if (!root.TryGetProperty("public_ip", out var ipEl) ||
            !root.TryGetProperty("public_port", out var portEl))
        {
            return null;
        }

        var ip = ipEl.GetString();
        var port = portEl.GetInt32();
        if (string.IsNullOrWhiteSpace(ip) || port <= 0)
        {
            return null;
        }

        return new IPEndPoint(IPAddress.Parse(ip), port);
    }

    private static async Task<IPEndPoint> ResolveServerAsync(string host, int port)
    {
        var addresses = await Dns.GetHostAddressesAsync(host);
        if (addresses.Length == 0)
        {
            throw new Exception($"Cannot resolve STUN server: {host}");
        }

        var address = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                      ?? addresses[0];
        return new IPEndPoint(address, port);
    }
}
