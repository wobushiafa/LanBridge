namespace LanBridge.Common.Configuration;

public class PeerConfig : PeerBaseConfig
{
    public PeerConfig()
    {
        NodeId = "intranet-peer-001";
    }

    public string Token { get; set; } = "default-token";
    public string TargetSourceHost { get; set; } = "127.0.0.1";
    public int TargetSourcePort { get; set; } = 554;
    public List<TargetEndpoint> AllowedTargets { get; set; } = new();
    public List<AllowedSubnet> AllowedSubnets { get; set; } = new();

    public override List<string> Validate()
    {
        var errors = base.Validate();

        if (string.IsNullOrWhiteSpace(TargetSourceHost))
            errors.Add("targetSourceHost is required.");
        if (TargetSourcePort is <= 0 or > 65535)
            errors.Add("targetSourcePort must be between 1 and 65535.");

        return errors;
    }
}