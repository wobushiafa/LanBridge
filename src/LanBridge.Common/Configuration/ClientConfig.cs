namespace LanBridge.Common.Configuration;

public class ClientConfig : PeerBaseConfig
{
    public ClientConfig()
    {
        NodeId = "extranet-client-001";
    }

    public string TargetNodeId { get; set; } = "intranet-peer-001";
    public int LocalProxyPort { get; set; } = 8554;
    public int HolePunchTimeoutMs { get; set; } = 10000;
    public bool EnableRelayFallback { get; set; } = true;
    public List<TunnelMapping> Mappings { get; set; } = new();

    public override List<string> Validate()
    {
        var errors = base.Validate();

        if (string.IsNullOrWhiteSpace(TargetNodeId))
            errors.Add("targetNodeId is required.");
        if (HolePunchTimeoutMs is < 1000 or > 60000)
            errors.Add("holePunchTimeoutMs must be between 1000 and 60000.");
        if (LocalProxyPort is < 0 or > 65535)
            errors.Add("localProxyPort must be between 0 and 65535.");

        foreach (var m in Mappings)
        {
            if (m.LocalPort is <= 0 or > 65535)
                errors.Add($"Mapping localPort {m.LocalPort} must be between 1 and 65535.");
            if (string.IsNullOrWhiteSpace(m.TargetHost))
                errors.Add($"Mapping localPort={m.LocalPort}: targetHost is required.");
            if (m.TargetPort is <= 0 or > 65535)
                errors.Add($"Mapping localPort={m.LocalPort}: targetPort must be between 1 and 65535.");
            if (m.Protocol != "tcp" && m.Protocol != "udp")
                errors.Add($"Mapping localPort={m.LocalPort}: protocol must be 'tcp' or 'udp', got '{m.Protocol}'.");
        }

        return errors;
    }
}