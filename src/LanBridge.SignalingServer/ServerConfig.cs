using LanBridge.Common.Configuration;

namespace LanBridge.SignalingServer;

public class ServerConfig
{
    public int SignalingPort { get; set; } = 9000;
    public int StunPort { get; set; } = 9001;
    public int StunAlternatePort { get; set; } = 9003;
    public int RelayPort { get; set; } = 9002;
    public int MaxRelaySessions { get; set; } = 100;
    public int RelayTimeoutMs { get; set; } = 30000;
    public bool RequireRegistrationToken { get; set; }
    public List<string> RegistrationTokens { get; set; } = new();
    public int MetricsReportIntervalSeconds { get; set; } = 30;

    public void Validate()
    {
        ConfigValidation.EnsurePort(SignalingPort, nameof(SignalingPort));
        ConfigValidation.EnsurePort(StunPort, nameof(StunPort));
        ConfigValidation.EnsurePort(StunAlternatePort, nameof(StunAlternatePort));
        ConfigValidation.EnsurePort(RelayPort, nameof(RelayPort));
        ConfigValidation.EnsurePositive(MaxRelaySessions, nameof(MaxRelaySessions));
        ConfigValidation.EnsurePositive(RelayTimeoutMs, nameof(RelayTimeoutMs));
        ConfigValidation.EnsurePositive(MetricsReportIntervalSeconds, nameof(MetricsReportIntervalSeconds));
        ConfigValidation.EnsureRegistrationTokenPolicy(RegistrationTokens, RequireRegistrationToken);
    }
}
