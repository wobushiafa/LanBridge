using LanBridge.ExtranetPeer;
using LanBridge.IntranetPeer;
using LanBridge.SignalingServer;
using Xunit;

namespace LanBridge.Tests;

public class ConfigurationTests
{
    [Fact]
    public void ClientConfig_Validate_AcceptsValidMappings()
    {
        var config = new ClientConfig
        {
            NodeId = "extranet-1",
            TargetNodeId = "intranet-1",
            SignalingServerHost = "signal.example.com",
            SignalingServerPort = 9000,
            StunServerHost = "stun.example.com",
            StunServerPort = 9001,
            StunAlternateServerPort = 9003,
            HolePunchTimeoutMs = 10000,
            Mappings =
            {
                new TunnelMapping
                {
                    LocalPort = 8554,
                    TargetHost = "192.168.1.10",
                    TargetPort = 554,
                    Protocol = "tcp"
                }
            }
        };

        config.Validate();
    }

    [Fact]
    public void ClientConfig_Validate_RejectsBadProtocol()
    {
        var config = new ClientConfig
        {
            NodeId = "extranet-1",
            TargetNodeId = "intranet-1",
            SignalingServerHost = "signal.example.com",
            SignalingServerPort = 9000,
            StunServerHost = "stun.example.com",
            StunServerPort = 9001,
            StunAlternateServerPort = 9003,
            HolePunchTimeoutMs = 10000,
            Mappings =
            {
                new TunnelMapping
                {
                    LocalPort = 8554,
                    TargetHost = "192.168.1.10",
                    TargetPort = 554,
                    Protocol = "sctp"
                }
            }
        };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void PeerConfig_Validate_RejectsBadSubnet()
    {
        var config = new PeerConfig
        {
            NodeId = "intranet-1",
            SignalingServerHost = "signal.example.com",
            SignalingServerPort = 9000,
            StunServerHost = "stun.example.com",
            StunServerPort = 9001,
            StunAlternateServerPort = 9003,
            TargetSourceHost = "192.168.1.10",
            TargetSourcePort = 554,
            AllowedSubnets =
            {
                new AllowedSubnet { Cidr = "192.168.1.0/40" }
            }
        };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void ServerConfig_Validate_RequiresTokensWhenEnabled()
    {
        var config = new ServerConfig
        {
            SignalingPort = 9000,
            StunPort = 9001,
            StunAlternatePort = 9003,
            RelayPort = 9002,
            MaxRelaySessions = 100,
            RelayTimeoutMs = 30000,
            MetricsReportIntervalSeconds = 30,
            RequireRegistrationToken = true
        };

        Assert.Throws<InvalidOperationException>(() => config.Validate());
    }

    [Fact]
    public void ServerConfig_Validate_AcceptsTokenProtectedSetup()
    {
        var config = new ServerConfig
        {
            SignalingPort = 9000,
            StunPort = 9001,
            StunAlternatePort = 9003,
            RelayPort = 9002,
            MaxRelaySessions = 100,
            RelayTimeoutMs = 30000,
            MetricsReportIntervalSeconds = 30,
            RequireRegistrationToken = true,
            RegistrationTokens = { "alpha-token" }
        };

        config.Validate();
    }
}
