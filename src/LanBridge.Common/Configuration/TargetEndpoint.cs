namespace LanBridge.Common.Configuration;

public class TargetEndpoint
{
    public string Host { get; set; } = string.Empty;
    public int? Port { get; set; }

    public override string ToString() => Port.HasValue ? $"{Host}:{Port}" : $"{Host}:*";
}
