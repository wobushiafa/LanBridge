namespace LanBridge.Common.Configuration;

public sealed class NodeIdentityOptions
{
    public string NodeId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
}

public sealed class EndpointOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; }
}

public sealed class StunEndpointOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 9001;
    public int AlternatePort { get; set; } = 9003;
}

public sealed class TransportOptions
{
    public int UdpPort { get; set; }
    public bool Verbose { get; set; }
    public bool EnableKcpCongestionControl { get; set; }
    public bool EnableTui { get; set; }
    public string SignalingTransport { get; set; } = "tcp";
    public int SignalingWsPort { get; set; } = 9010;
    public bool EnablePortMapping { get; set; }
    public int ExternalPort { get; set; }
}

public sealed class ExtranetConnectionOptions
{
    public string TargetNodeId { get; set; } = string.Empty;
    public int HolePunchTimeoutMs { get; set; } = 10000;
    public bool EnableRelayFallback { get; set; } = true;
}

public sealed class ProxyListenerOptions
{
    public int LocalPort { get; set; } = 8554;
}

public sealed class IntranetTargetOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 554;
}

public sealed class ServerPortOptions
{
    public int SignalingPort { get; set; } = 9000;
    public int StunPort { get; set; } = 9001;
    public int StunAlternatePort { get; set; } = 9003;
    public int RelayPort { get; set; } = 9002;
    public int WebSocketPort { get; set; }
}

public sealed class ServerRelayOptions
{
    public int MaxSessions { get; set; } = 100;
    public int IdleTimeoutMs { get; set; } = 30000;
}

public sealed class ServerSecurityOptions
{
    public bool RequireRegistrationToken { get; set; }
    public List<string> RegistrationTokens { get; set; } = new();
}

public sealed class MetricsOptions
{
    public int ReportIntervalSeconds { get; set; } = 30;
}
