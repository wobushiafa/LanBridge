using System.Net;
using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

public class NodeInfo
{
    public string NodeId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public IPEndPoint? PublicEndPoint { get; set; }
    public IPEndPoint? PublicEndPointV6 { get; set; }
    public DateTime LastSeen { get; set; }
    public bool IsIntranet { get; set; }
    public StunNatType NatType { get; set; } = StunNatType.Unknown;
}
