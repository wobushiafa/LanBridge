using LanBridge.Common.Configuration;
using System.Text.Json.Serialization;

namespace LanBridge.SignalingServer;

public class ServerConfig
{
    [JsonIgnore]
    public ServerPortOptions Ports { get; } = new();

    [JsonIgnore]
    public ServerRelayOptions Relay { get; } = new();

    [JsonIgnore]
    public ServerSecurityOptions Security { get; } = new();

    [JsonIgnore]
    public MetricsOptions Metrics { get; } = new();

    public int SignalingPort { get => Ports.SignalingPort; set => Ports.SignalingPort = value; }
    public int StunPort { get => Ports.StunPort; set => Ports.StunPort = value; }
    public int StunAlternatePort { get => Ports.StunAlternatePort; set => Ports.StunAlternatePort = value; }
    public int RelayPort { get => Ports.RelayPort; set => Ports.RelayPort = value; }
    public int WebSocketPort { get => Ports.WebSocketPort; set => Ports.WebSocketPort = value; }
    public int MaxRelaySessions { get => Relay.MaxSessions; set => Relay.MaxSessions = value; }
    public int RelayTimeoutMs { get => Relay.IdleTimeoutMs; set => Relay.IdleTimeoutMs = value; }
    public bool RequireRegistrationToken { get => Security.RequireRegistrationToken; set => Security.RequireRegistrationToken = value; }
    public List<string> RegistrationTokens { get => Security.RegistrationTokens; set => Security.RegistrationTokens = value ?? new(); }
    public int MetricsReportIntervalSeconds { get => Metrics.ReportIntervalSeconds; set => Metrics.ReportIntervalSeconds = value; }

    public void Validate()
    {
        ConfigValidation.EnsurePort(Ports.SignalingPort, nameof(Ports.SignalingPort));
        ConfigValidation.EnsurePort(Ports.StunPort, nameof(Ports.StunPort));
        ConfigValidation.EnsurePort(Ports.StunAlternatePort, nameof(Ports.StunAlternatePort));
        ConfigValidation.EnsurePort(Ports.RelayPort, nameof(Ports.RelayPort));
        if (Ports.WebSocketPort != 0)
        {
            ConfigValidation.EnsurePort(Ports.WebSocketPort, nameof(Ports.WebSocketPort));
        }
        ConfigValidation.EnsurePositive(Relay.MaxSessions, nameof(Relay.MaxSessions));
        ConfigValidation.EnsurePositive(Relay.IdleTimeoutMs, nameof(Relay.IdleTimeoutMs));
        ConfigValidation.EnsurePositive(Metrics.ReportIntervalSeconds, nameof(Metrics.ReportIntervalSeconds));
        ConfigValidation.EnsureRegistrationTokenPolicy(Security.RegistrationTokens, Security.RequireRegistrationToken);
    }
}
