namespace LanBridge.Common.Configuration;

public class ServerConfig
{
    public int SignalingPort { get; set; } = 9000;
    public int StunPort { get; set; } = 9001;
    public int StunAlternatePort { get; set; } = 9003;
    public int RelayPort { get; set; } = 9002;
    public int MaxRelaySessions { get; set; } = 100;

    public List<string> Validate()
    {
        var errors = new List<string>();

        if (SignalingPort is <= 0 or > 65535)
            errors.Add("signalingPort must be between 1 and 65535.");
        if (StunPort is <= 0 or > 65535)
            errors.Add("stunPort must be between 1 and 65535.");
        if (RelayPort is <= 0 or > 65535)
            errors.Add("relayPort must be between 1 and 65535.");
        if (MaxRelaySessions is < 1 or > 10000)
            errors.Add("maxRelaySessions must be between 1 and 10000.");

        return errors;
    }
}