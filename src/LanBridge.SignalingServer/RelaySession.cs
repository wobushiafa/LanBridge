using System.Net.Sockets;

namespace LanBridge.SignalingServer;

public class RelaySession
{
    public string SessionId { get; set; } = string.Empty;
    public string IntranetClientId { get; set; } = string.Empty;
    public string ExtranetClientId { get; set; } = string.Empty;
    public TcpClient? IntranetClient { get; set; }
    public TcpClient? ExtranetClient { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime LastActivityUtc { get; set; }
    public bool IsActive { get; set; }
}
