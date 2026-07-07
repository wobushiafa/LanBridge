namespace LanBridge.Common.Configuration;

public class AllowedSubnet
{
    public string Cidr { get; set; } = string.Empty;
    public int? Port { get; set; }

    public override string ToString() => Port.HasValue ? $"{Cidr}:{Port}" : $"{Cidr}:*";
}
