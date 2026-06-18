using System.Net;
using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public sealed record NatProbeSnapshot(
    NatDetectionResult Detection,
    IPEndPoint? PublicEndPoint,
    IPEndPoint? PublicEndPointV6);

public sealed class PeerNatDiagnostics
{
    private readonly PeerConnectionOptions _options;
    private readonly Action<string>? _statusSink;

    public PeerNatDiagnostics(PeerConnectionOptions options, Action<string>? statusSink)
    {
        _options = options;
        _statusSink = statusSink;
    }

    public async Task<NatProbeSnapshot> DetectAsync(UdpHolePuncher holePuncher)
    {
        _statusSink?.Invoke("Getting public endpoint via standard STUN...");
        _statusSink?.Invoke($"STUN server: {_options.StunServerHost}:{_options.StunServerPort}");
        _statusSink?.Invoke($"Local endpoint: {holePuncher.LocalEndPoint}");

        var detection = await StunClient.DetectNatAsync(
            holePuncher,
            _options.StunServerHost,
            _options.StunServerPort,
            _options.StunAlternateServerPort);

        var publicEndPoint = detection.PublicEndPoint;
        IPEndPoint? publicEndPointV6 = null;

        try
        {
            publicEndPointV6 = await StunClient.QueryPublicEndPointV6Async(
                holePuncher,
                _options.StunServerHost,
                _options.StunServerPort,
                timeoutMs: 2000);
            if (publicEndPointV6 != null)
            {
                _statusSink?.Invoke($"IPv6 public endpoint: {publicEndPointV6}");
            }
        }
        catch (Exception ex)
        {
            if (_options.Verbose)
            {
                _statusSink?.Invoke($"IPv6 STUN query failed: {ex.Message}");
            }
        }

        if (publicEndPoint == null && publicEndPointV6 == null)
        {
            _statusSink?.Invoke("Configured STUN server is unavailable. Attempting fallback to public Google STUN server (stun.l.google.com:19302)...");
            (detection, publicEndPoint, publicEndPointV6) = await TryFallbackToGoogleStunAsync(holePuncher, detection, publicEndPointV6);
        }

        if (publicEndPoint == null && publicEndPointV6 == null)
        {
            _statusSink?.Invoke($"STUN unavailable (both v4/v6) after Google STUN fallback, continuing with relay fallback: {detection.Reason}");
            return new NatProbeSnapshot(detection, publicEndPoint, publicEndPointV6);
        }

        if (publicEndPoint != null && detection.PublicEndPoint != null)
        {
            var mapping = detection.PortPreserved ? "port-preserved" : "port-mapped";
            _statusSink?.Invoke($"Public endpoint (IPv4): {publicEndPoint}");
            _statusSink?.Invoke($"NAT mapping: {holePuncher.LocalEndPoint} -> {publicEndPoint} ({mapping})");
            _statusSink?.Invoke($"NAT type: {FormatNatType(detection.NatType)}");
            _statusSink?.Invoke($"NAT diagnosis: {detection.Reason}");
        }

        return new NatProbeSnapshot(detection, publicEndPoint, publicEndPointV6);
    }

    public Task RunKeepAliveLoopAsync(
        UdpHolePuncher holePuncher,
        CancellationToken cancellationToken,
        Func<bool> shouldProbe,
        Func<IPEndPoint?> currentPublicEndPointProvider,
        Func<IPEndPoint, Task> onMappingChangedAsync)
    {
        return Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(25));
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!shouldProbe())
                {
                    continue;
                }

                try
                {
                    var result = await StunClient.QueryAsync(
                        holePuncher,
                        _options.StunServerHost,
                        _options.StunServerPort,
                        timeoutMs: 2000);

                    if (result.PublicEndPoint != null && !result.PublicEndPoint.Equals(currentPublicEndPointProvider()))
                    {
                        _statusSink?.Invoke($"NAT mapping changed from {currentPublicEndPointProvider()} to {result.PublicEndPoint}. Re-registering...");
                        await onMappingChangedAsync(result.PublicEndPoint);
                    }
                }
                catch (Exception ex)
                {
                    if (_options.Verbose)
                    {
                        _statusSink?.Invoke($"NAT keep-alive failed: {ex.Message}");
                    }
                }
            }
        }, cancellationToken);
    }

    private async Task<(NatDetectionResult Detection, IPEndPoint? PublicEndPoint, IPEndPoint? PublicEndPointV6)> TryFallbackToGoogleStunAsync(
        UdpHolePuncher holePuncher,
        NatDetectionResult detection,
        IPEndPoint? publicEndPointV6)
    {
        IPEndPoint? publicEndPoint = null;

        try
        {
            var fallbackResult = await StunClient.QueryAsync(holePuncher, "stun.l.google.com", 19302, timeoutMs: 2500);
            publicEndPoint = fallbackResult.PublicEndPoint;
            detection = new NatDetectionResult(
                StunNatType.Unknown,
                "NAT classified via Google STUN fallback",
                publicEndPoint,
                null,
                fallbackResult.PublicEndPoint.Port == (holePuncher.LocalEndPoint?.Port ?? 0));

            var mapping = detection.PortPreserved ? "port-preserved" : "port-mapped";
            _statusSink?.Invoke($"Public endpoint (IPv4) via Google STUN: {publicEndPoint}");
            _statusSink?.Invoke($"NAT mapping: {holePuncher.LocalEndPoint} -> {publicEndPoint} ({mapping})");
        }
        catch (Exception fallbackEx)
        {
            if (_options.Verbose)
            {
                _statusSink?.Invoke($"IPv4 STUN Fallback to Google failed: {fallbackEx.Message}");
            }
        }

        try
        {
            publicEndPointV6 = await StunClient.QueryPublicEndPointV6Async(
                holePuncher,
                "stun.l.google.com",
                19302,
                timeoutMs: 2500);
            if (publicEndPointV6 != null)
            {
                _statusSink?.Invoke($"IPv6 public endpoint via Google STUN: {publicEndPointV6}");
            }
        }
        catch (Exception fallbackExV6)
        {
            if (_options.Verbose)
            {
                _statusSink?.Invoke($"IPv6 STUN Fallback to Google failed: {fallbackExV6.Message}");
            }
        }

        return (detection, publicEndPoint, publicEndPointV6);
    }

    private static string FormatNatType(StunNatType natType)
    {
        return natType switch
        {
            StunNatType.FullCone => "Full Cone",
            StunNatType.RestrictedCone => "Restricted Cone",
            StunNatType.PortRestrictedCone => "Port Restricted Cone",
            StunNatType.Symmetric => "Symmetric",
            StunNatType.Blocked => "Blocked",
            _ => "Unknown"
        };
    }
}
