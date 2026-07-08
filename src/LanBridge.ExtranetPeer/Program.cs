using LanBridge.Common.Configuration;
using LanBridge.Common.Runtime;

namespace LanBridge.ExtranetPeer;

/// <summary>
/// 外网客户端节点主程序
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        ConsoleStatusWriter.WriteHeader("LanBridge Extranet Client");
        
        var config = LoadConfig(args) ?? new ClientConfig();
        ParseArguments(args, config);
        EnsureDefaultMapping(config);
        config.Validate();
        
        var configurationItems = new List<(string Label, string Value)>
        {
            ("Identity", config.Identity.NodeId),
            ("Signaling", $"{config.Signaling.Host}:{config.Signaling.Port}"),
            ("STUN", $"{config.Stun.Host}:{config.Stun.Port}"),
            ("STUN Alternate Port", config.Stun.AlternatePort.ToString()),
            ("Connection Target", config.Connection.TargetNodeId),
            ("Proxy Port", config.Proxy.LocalPort.ToString()),
            ("Hole Punch Timeout", $"{config.Connection.HolePunchTimeoutMs}ms"),
            ("Relay Fallback", config.Connection.EnableRelayFallback ? "enabled" : "disabled"),
            ("Signaling Transport", config.Transport.SignalingTransport),
            ("WebSocket Port", config.Transport.SignalingWsPort.ToString()),
            ("Verbose", config.Transport.Verbose ? "enabled" : "disabled")
        };
        configurationItems.AddRange(config.Mappings.Select(mapping =>
            ("Mapping", $"127.0.0.1:{mapping.LocalPort} -> {(string.IsNullOrWhiteSpace(mapping.Target) ? "intranet default target" : mapping.Target)}")));
        ConsoleStatusWriter.WriteConfiguration(configurationItems);
        
        using var peer = new ExtranetPeer(config);
        
        peer.OnStatusChanged += status =>
        {
            ConsoleStatusWriter.WritePeerStatus(status);
        };

        var trafficLock = new object();
        long remoteBytes = 0;
        var lastTrafficLog = DateTime.UtcNow;

        peer.OnDataReceived += (data, length) =>
        {
            lock (trafficLock)
            {
                remoteBytes += length;
                if (DateTime.UtcNow - lastTrafficLog < TimeSpan.FromSeconds(2))
                {
                    return;
                }

                var mode = peer.State == ConnectionState.RelayMode ? "RELAY" :
                    peer.State == ConnectionState.Connected ? "P2P" : peer.State.ToString();
                ConsoleStatusWriter.WriteColored($"[{DateTime.Now:HH:mm:ss}] Traffic: {remoteBytes / 1024.0:F1} KB from remote via {mode}", ConsoleColor.DarkGray);
                remoteBytes = 0;
                lastTrafficLog = DateTime.UtcNow;
            }
        };
        
        using var cts = new CancellationTokenSource();
        
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            peer.Dispose();
            cts.Cancel();
        };
        
        try
        {
            await peer.StartAsync();
        }
        catch (OperationCanceledException)
        {
            // 正常关闭
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
        
        Console.WriteLine("Client stopped.");
    }

    private static void ParseArguments(string[] args, ClientConfig config)
    {
        long? pendingRateLimit = null;
        string? pendingPriority = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config":
                case "-c":
                    if (i + 1 < args.Length)
                        i++;
                    break;

                case "--node-id":
                case "-id":
                    if (i + 1 < args.Length)
                        config.NodeId = args[++i];
                    break;
                
                case "--signaling-host":
                case "-sh":
                    if (i + 1 < args.Length)
                        config.SignalingServerHost = args[++i];
                    break;
                
                case "--signaling-port":
                case "-sp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int sp))
                        config.SignalingServerPort = sp;
                    break;
                
                case "--stun-host":
                case "-sth":
                    if (i + 1 < args.Length)
                        config.StunServerHost = args[++i];
                    break;
                
                case "--stun-port":
                case "-stp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int stp))
                        config.StunServerPort = stp;
                    break;

                case "--stun-alt-port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int sap))
                        config.StunAlternateServerPort = sap;
                    break;
                
                case "--target-node":
                case "-tn":
                    if (i + 1 < args.Length)
                        config.TargetNodeId = args[++i];
                    break;
                
                case "--local-port":
                case "-lp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int lp))
                        config.LocalProxyPort = lp;
                    break;

                case "--map":
                case "-m":
                    if (i + 1 < args.Length && TryParseMapping(args[++i], out var mapping))
                    {
                        if (pendingRateLimit.HasValue)
                        {
                            mapping.RateLimitBytesPerSec = pendingRateLimit.Value;
                            pendingRateLimit = null;
                        }
                        if (pendingPriority != null)
                        {
                            mapping.Priority = pendingPriority;
                            pendingPriority = null;
                        }
                        config.Mappings.Add(mapping);
                    }
                    break;

                case "--rate-limit":
                    if (i + 1 < args.Length && long.TryParse(args[++i], out long rl))
                    {
                        if (config.Mappings.Count > 0)
                            config.Mappings[^1].RateLimitBytesPerSec = rl;
                        else
                            pendingRateLimit = rl;
                    }
                    break;

                case "--priority":
                    if (i + 1 < args.Length)
                    {
                        var p = args[++i].ToLowerInvariant();
                        if (p == "high" || p == "normal" || p == "low")
                        {
                            if (config.Mappings.Count > 0)
                                config.Mappings[^1].Priority = p;
                            else
                                pendingPriority = p;
                        }
                    }
                    break;
                
                case "--udp-port":
                case "-up":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int up))
                        config.UdpPort = up;
                    break;
                
                case "--punch-timeout":
                case "-pt":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int pt))
                        config.HolePunchTimeoutMs = pt;
                    break;
                
                case "--no-relay":
                    config.EnableRelayFallback = false;
                    break;

                case "--signaling-transport":
                    if (i + 1 < args.Length)
                        config.SignalingTransport = args[++i];
                    break;

                case "--ws-port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int wsPort))
                        config.SignalingWsPort = wsPort;
                    break;

                case "--verbose":
                case "-v":
                    config.Verbose = true;
                    break;

                case "--tui":
                case "--dashboard":
                    config.Transport.EnableTui = true;
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static ClientConfig? LoadConfig(string[] args)
    {
        return JsonConfigFile.Load(
            args,
            ExtranetConfigJsonContext.Default.ClientConfig,
            ConfigJsonCompatibility.NormalizeExtranet,
            "--config",
            "-c");
    }

    private static void EnsureDefaultMapping(ClientConfig config)
    {
        if (config.Mappings.Count > 0)
        {
            return;
        }

        config.Mappings.Add(new TunnelMapping { LocalPort = config.LocalProxyPort });
    }

    private static bool TryParseMapping(string value, out TunnelMapping mapping)
    {
        mapping = new TunnelMapping();
        var equalsIndex = value.IndexOf('=');
        if (equalsIndex <= 0 || equalsIndex == value.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(value[..equalsIndex], out var localPort))
        {
            return false;
        }

        var targetPart = value[(equalsIndex + 1)..];
        if (!LanBridge.Common.Protocol.TargetDescriptorParser.TryParse(targetPart, out var descriptor))
        {
            return false;
        }

        mapping = new TunnelMapping
        {
            LocalPort = localPort,
            TargetHost = descriptor.Host,
            TargetPort = descriptor.Port,
            Protocol = descriptor.Protocol,
            TargetNodeId = descriptor.NodeId
        };
        return true;
    }
    
    private static void PrintHelp()
    {
        Console.WriteLine("Usage: LanBridge.ExtranetPeer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --node-id, -id <id>             Node identifier (default: extranet-client-001)");
        Console.WriteLine("  --signaling-host, -sh <host>    Signaling server host (default: 127.0.0.1)");
        Console.WriteLine("  --signaling-port, -sp <port>    Signaling server port (default: 9000)");
        Console.WriteLine("  --stun-host, -sth <host>        STUN server host (default: 127.0.0.1)");
        Console.WriteLine("  --stun-port, -stp <port>        STUN server port (default: 9001)");
        Console.WriteLine("  --stun-alt-port <port>          Alternate STUN port for NAT detection (default: 9003)");
        Console.WriteLine("  --config, -c <path>             Load JSON config file");
        Console.WriteLine("  --target-node, -tn <id>         Target intranet node ID (default: intranet-peer-001)");
        Console.WriteLine("  --local-port, -lp <port>        Local proxy port (default: 8554)");
        Console.WriteLine("  --map, -m <local=host:port[:proto]> Add tunnel mapping, e.g. 8554=192.168.7.230:554 or 53=8.8.8.8:53:udp");
        Console.WriteLine("  --rate-limit <bps>              Rate limit (bytes/sec) for the most recent --map (0 = unlimited)");
        Console.WriteLine("  --priority <high|normal|low>   QoS priority for the most recent --map (UDP defaults high, TCP normal)");
        Console.WriteLine("  --udp-port, -up <port>          UDP port for P2P (default: random)");
        Console.WriteLine("  --punch-timeout, -pt <ms>       Hole punch timeout in ms (default: 10000)");
        Console.WriteLine("  --no-relay                      Disable relay fallback");
        Console.WriteLine("  --signaling-transport <tcp|ws|auto> Signaling transport (default: tcp)");
        Console.WriteLine("  --ws-port <port>                WebSocket signaling port (default: 9010)");
        Console.WriteLine("  --verbose, -v                   Enable detailed KCP diagnostics");
        Console.WriteLine("  --help, -h                      Show this help");
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ClientConfig))]
internal partial class ExtranetConfigJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
