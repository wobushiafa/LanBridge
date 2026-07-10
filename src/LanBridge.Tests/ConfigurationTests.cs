using System.Text.Json;
using System.Text.Json.Nodes;
using LanBridge.Common.Configuration;
using LanBridge.ExtranetPeer;
using LanBridge.IntranetPeer;
using LanBridge.SignalingServer;
using Xunit;

namespace LanBridge.Tests;

public class ConfigurationTests
{
    [Fact]
    public void ClientConfig_GroupedViews_StayInSyncWithFlatProperties()
    {
        var config = new ClientConfig
        {
            NodeId = "extranet-1",
            SignalingServerHost = "signal.example.com",
            SignalingServerPort = 9000,
            StunServerHost = "stun.example.com",
            StunServerPort = 9001,
            StunAlternateServerPort = 9003,
            TargetNodeId = "intranet-1",
            LocalProxyPort = 8554,
            HolePunchTimeoutMs = 12000,
            EnableRelayFallback = false,
            Verbose = true
        };

        Assert.Equal("extranet-1", config.Identity.NodeId);
        Assert.Equal("signal.example.com", config.Signaling.Host);
        Assert.Equal(9000, config.Signaling.Port);
        Assert.Equal("stun.example.com", config.Stun.Host);
        Assert.Equal(9001, config.Stun.Port);
        Assert.Equal(9003, config.Stun.AlternatePort);
        Assert.Equal("intranet-1", config.Connection.TargetNodeId);
        Assert.Equal(8554, config.Proxy.LocalPort);
        Assert.Equal(12000, config.Connection.HolePunchTimeoutMs);
        Assert.False(config.Connection.EnableRelayFallback);
        Assert.True(config.Transport.Verbose);
    }

    [Fact]
    public void ClientConfig_GroupedJson_MapsToFlatPropertiesWithoutBreakingDefaults()
    {
        var config = DeserializeConfig<ClientConfig>(
            ConfigJsonCompatibility.NormalizeExtranet(ParseObject(
                """
                {
                  "identity": {
                    "nodeId": "extranet-nested"
                  },
                  "signaling": {
                    "host": "signal.example.com"
                  },
                  "stun": {
                    "host": "stun.example.com"
                  },
                  "connection": {
                    "targetNodeId": "intranet-nested"
                  },
                  "proxy": {
                    "localPort": 9009
                  },
                  "transport": {
                    "verbose": true
                  }
                }
                """)));

        Assert.Equal("extranet-nested", config.NodeId);
        Assert.Equal("signal.example.com", config.SignalingServerHost);
        Assert.Equal(9000, config.SignalingServerPort);
        Assert.Equal("stun.example.com", config.StunServerHost);
        Assert.Equal(9001, config.StunServerPort);
        Assert.Equal("intranet-nested", config.TargetNodeId);
        Assert.Equal(9009, config.LocalProxyPort);
        Assert.True(config.Verbose);
    }

    [Fact]
    public void ClientConfig_FlatPropertiesOverrideGroupedJsonValues()
    {
        var config = DeserializeConfig<ClientConfig>(
            ConfigJsonCompatibility.NormalizeExtranet(ParseObject(
                """
                {
                  "signalingServerPort": 9100,
                  "signaling": {
                    "port": 9200
                  }
                }
                """)));

        Assert.Equal(9100, config.SignalingServerPort);
    }

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
    public void ClientConfig_Validate_RejectsBadSignalingTransport()
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
            SignalingTransport = "quic",
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

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("tcp, ws, or auto", ex.Message);
    }

    [Fact]
    public void ClientConfig_Validate_RequiresWebSocketPortForWsTransport()
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
            SignalingTransport = "ws",
            SignalingWsPort = 0,
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

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("ws or auto", ex.Message);
    }

    [Fact]
    public void PeerConfig_GroupedViews_StayInSyncWithFlatProperties()
    {
        var config = new PeerConfig
        {
            NodeId = "intranet-1",
            Token = "peer-token",
            SignalingServerHost = "signal.example.com",
            SignalingServerPort = 9000,
            StunServerHost = "stun.example.com",
            StunServerPort = 9001,
            StunAlternateServerPort = 9003,
            TargetSourceHost = "192.168.1.10",
            TargetSourcePort = 554,
            UdpPort = 34567,
            Verbose = true,
            EnableKcpCongestionControl = true
        };

        Assert.Equal("intranet-1", config.Identity.NodeId);
        Assert.Equal("peer-token", config.Identity.Token);
        Assert.Equal("signal.example.com", config.Signaling.Host);
        Assert.Equal(9000, config.Signaling.Port);
        Assert.Equal("stun.example.com", config.Stun.Host);
        Assert.Equal(9001, config.Stun.Port);
        Assert.Equal(9003, config.Stun.AlternatePort);
        Assert.Equal("192.168.1.10", config.Target.Host);
        Assert.Equal(554, config.Target.Port);
        Assert.Equal(34567, config.Transport.UdpPort);
        Assert.True(config.Transport.Verbose);
        Assert.True(config.Transport.EnableKcpCongestionControl);
    }

    [Fact]
    public void PeerConfig_GroupedJson_MapsToFlatPropertiesWithoutBreakingDefaults()
    {
        var config = DeserializeConfig<PeerConfig>(
            ConfigJsonCompatibility.NormalizeIntranet(ParseObject(
                """
                {
                  "identity": {
                    "nodeId": "intranet-nested",
                    "token": "nested-token"
                  },
                  "signaling": {
                    "host": "signal.example.com"
                  },
                  "target": {
                    "host": "192.168.1.20"
                  },
                  "transport": {
                    "udpPort": 45678,
                    "enableKcpCongestionControl": true
                  }
                }
                """)));

        Assert.Equal("intranet-nested", config.NodeId);
        Assert.Equal("nested-token", config.Token);
        Assert.Equal("signal.example.com", config.SignalingServerHost);
        Assert.Equal(9000, config.SignalingServerPort);
        Assert.Equal("192.168.1.20", config.TargetSourceHost);
        Assert.Equal(554, config.TargetSourcePort);
        Assert.Equal(45678, config.UdpPort);
        Assert.True(config.EnableKcpCongestionControl);
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
    public void PeerConfig_Validate_RejectsBadSignalingTransport()
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
            SignalingTransport = "http3"
        };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("tcp, ws, or auto", ex.Message);
    }

    [Fact]
    public void PeerConfig_Validate_RequiresWebSocketPortForAutoTransport()
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
            SignalingTransport = "auto",
            SignalingWsPort = 70000
        };

        var ex = Assert.Throws<InvalidOperationException>(() => config.Validate());
        Assert.Contains("ws or auto", ex.Message);
    }

    [Fact]
    public void SignalingConnectionLoop_RejectsUnsupportedTransport()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => new LanBridge.Common.Network.SignalingConnectionLoop(
            "127.0.0.1",
            9000,
            statusSink: null,
            onDisconnected: null,
            onMessageAsync: _ => Task.CompletedTask,
            onConnectedAsync: _ => Task.CompletedTask,
            transportType: "bogus",
            wsPort: 9010));

        Assert.Contains("tcp, ws, or auto", ex.Message);
    }

    [Fact]
    public void ServerConfig_GroupedViews_StayInSyncWithFlatProperties()
    {
        var config = new ServerConfig
        {
            SignalingPort = 9000,
            StunPort = 9001,
            StunAlternatePort = 9003,
            RelayPort = 9002,
            MaxRelaySessions = 150,
            RelayTimeoutMs = 45000,
            RequireRegistrationToken = true,
            MetricsReportIntervalSeconds = 15
        };
        config.RegistrationTokens.Add("alpha-token");

        Assert.Equal(9000, config.Ports.SignalingPort);
        Assert.Equal(9001, config.Ports.StunPort);
        Assert.Equal(9003, config.Ports.StunAlternatePort);
        Assert.Equal(9002, config.Ports.RelayPort);
        Assert.Equal(150, config.Relay.MaxSessions);
        Assert.Equal(45000, config.Relay.IdleTimeoutMs);
        Assert.True(config.Security.RequireRegistrationToken);
        Assert.Single(config.Security.RegistrationTokens);
        Assert.Equal(15, config.Metrics.ReportIntervalSeconds);
    }

    [Fact]
    public void ServerConfig_GroupedJson_MapsToFlatPropertiesWithoutBreakingDefaults()
    {
        var config = DeserializeConfig<ServerConfig>(
            ConfigJsonCompatibility.NormalizeServer(ParseObject(
                """
                {
                  "ports": {
                    "signalingPort": 9100
                  },
                  "relay": {
                    "maxSessions": 150
                  },
                  "security": {
                    "requireRegistrationToken": true,
                    "registrationTokens": [
                      "nested-token"
                    ]
                  },
                  "metrics": {
                    "reportIntervalSeconds": 15
                  }
                }
                """)));

        Assert.Equal(9100, config.SignalingPort);
        Assert.Equal(9001, config.StunPort);
        Assert.Equal(150, config.MaxRelaySessions);
        Assert.True(config.RequireRegistrationToken);
        Assert.Equal(new[] { "nested-token" }, config.RegistrationTokens);
        Assert.Equal(15, config.MetricsReportIntervalSeconds);
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

    private static JsonObject ParseObject(string json)
    {
        return JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidOperationException("Expected JSON object.");
    }

    private static T DeserializeConfig<T>(JsonObject root)
    {
        return JsonSerializer.Deserialize<T>(
            root.ToJsonString(),
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException("Expected deserialized configuration.");
    }
}
