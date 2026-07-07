namespace LanBridge.Common.Configuration;

public class PeerBaseConfig
{
    public string NodeId { get; set; } = "peer-001";
    public string SignalingServerHost { get; set; } = "127.0.0.1";
    public int SignalingServerPort { get; set; } = 9000;
    public string StunServerHost { get; set; } = "127.0.0.1";
    public int StunServerPort { get; set; } = 9001;
    public int StunAlternateServerPort { get; set; } = 9003;
    public int UdpPort { get; set; }
    public bool Verbose { get; set; }
    public bool EnableKcpCongestionControl { get; set; } = false;

    public virtual List<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(NodeId))
            errors.Add("nodeId is required.");
        if (string.IsNullOrWhiteSpace(SignalingServerHost))
            errors.Add("signalingServerHost is required.");
        if (SignalingServerPort is <= 0 or > 65535)
            errors.Add("signalingServerPort must be between 1 and 65535.");
        if (string.IsNullOrWhiteSpace(StunServerHost))
            errors.Add("stunServerHost is required.");
        if (StunServerPort is <= 0 or > 65535)
            errors.Add("stunServerPort must be between 1 and 65535.");
        if (UdpPort < 0 || UdpPort > 65535)
            errors.Add("udpPort must be between 0 and 65535.");

        return errors;
    }
}