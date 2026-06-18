using LanBridge.Common.Protocol;

namespace LanBridge.Common.Network;

public static class P2pFailureExplainer
{
    public static string Describe(NatDetectionResult? detection)
    {
        if (detection == null)
        {
            return "NAT was not detected because STUN did not complete; relay is the safest fallback";
        }

        return detection.NatType switch
        {
            StunNatType.Symmetric => "Symmetric NAT changed the public mapping across STUN endpoints, so peer-predicted UDP ports are unreliable; use relay/TURN-like fallback",
            StunNatType.PortRestrictedCone => "Port-restricted filtering likely blocked packets from the peer until both sides punch at the same time; retry may work, relay is expected fallback",
            StunNatType.Blocked => "STUN binding failed, so UDP traversal is blocked or unreachable; relay is required",
            StunNatType.Unknown => $"NAT type is unknown: {detection.Reason}; relay fallback is recommended",
            StunNatType.RestrictedCone => "NAT looked traversal-friendly, so failure is likely firewall filtering, endpoint mismatch, packet loss, or peer offline",
            StunNatType.FullCone => "NAT looked traversal-friendly, so failure is likely firewall filtering, endpoint mismatch, packet loss, or peer offline",
            _ => detection.Reason
        };
    }
}
