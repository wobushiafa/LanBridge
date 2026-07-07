namespace LanBridge.Common.Configuration;

public class TunnelMapping
{
    public int LocalPort { get; set; }
    public string TargetHost { get; set; } = string.Empty;
    public int TargetPort { get; set; }
    public string Protocol { get; set; } = "tcp";

    public string Target => string.IsNullOrWhiteSpace(TargetHost) || TargetPort <= 0
        ? string.Empty
        : $"{TargetHost}:{TargetPort}:{Protocol}";
}
