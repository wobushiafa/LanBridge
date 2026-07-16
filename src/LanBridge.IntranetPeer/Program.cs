using LanBridge.Common.Configuration;
using LanBridge.Common.Runtime;

namespace LanBridge.IntranetPeer;

/// <summary>
/// 内网代理节点主程序
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        ConsoleStatusWriter.WriteHeader("LanBridge Intranet Peer");
        
        var config = LoadConfig(args) ?? new PeerConfig();
        ParseArguments(args, config);
        EnsureDefaultAllowedTarget(config);
        config.Validate();
        
        ConsoleStatusWriter.WriteConfiguration(new[]
        {
            ("Identity", config.Identity.NodeId),
            ("Signaling", $"{config.Signaling.Host}:{config.Signaling.Port}"),
            ("STUN", $"{config.Stun.Host}:{config.Stun.Port}"),
            ("STUN Alternate Port", config.Stun.AlternatePort.ToString()),
            ("Target Source", $"{config.Target.Host}:{config.Target.Port}"),
            ("Allowed Targets", string.Join(", ", config.AllowedTargets)),
            ("Allowed Subnets", string.Join(", ", config.AllowedSubnets)),
            ("Signaling Transport", config.Transport.SignalingTransport),
            ("WebSocket Port", config.Transport.SignalingWsPort.ToString()),
            ("Port Mapping (UPnP)", config.Transport.EnablePortMapping ? $"enabled (external port: {config.Transport.ExternalPort})" : "disabled"),
            ("Verbose", config.Transport.Verbose ? "enabled" : "disabled")
        });
        
        using var peer = new IntranetPeer(config);
        
        peer.OnStatusChanged += status =>
        {
            ConsoleStatusWriter.WritePeerStatus(status);
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
        
        Console.WriteLine("Peer stopped.");
    }

    private static void ParseArguments(string[] args, PeerConfig config)
    {
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
                
                case "--token":
                case "-t":
                    if (i + 1 < args.Length)
                        config.Token = args[++i];
                    break;
                
                case "--target-host":
                case "-th":
                    if (i + 1 < args.Length)
                        config.TargetSourceHost = args[++i];
                    break;
                
                case "--target-port":
                case "-tp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int tp))
                        config.TargetSourcePort = tp;
                    break;

                case "--allow-target":
                case "-at":
                    if (i + 1 < args.Length && TryParseTarget(args[++i], out var target))
                        config.AllowedTargets.Add(target);
                    break;

                case "--allow-targets":
                    if (i + 1 < args.Length)
                        AddTargets(args[++i], config);
                    break;

                case "--allow-subnet":
                    if (i + 1 < args.Length && TryParseSubnet(args[++i], out var subnet))
                        config.AllowedSubnets.Add(subnet);
                    break;

                case "--allow-subnets":
                    if (i + 1 < args.Length)
                        AddSubnets(args[++i], config);
                    break;
                
                case "--udp-port":
                case "-up":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int up))
                        config.UdpPort = up;
                    break;

                case "--signaling-transport":
                    config.SignalingTransport = ReadValidatedSignalingTransport(args, ref i, "--signaling-transport");
                    break;

                case "--ws-port":
                    config.SignalingWsPort = ReadIntArgument(args, ref i, "--ws-port");
                    break;

                case "--verbose":
                case "-v":
                    config.Verbose = true;
                    break;

                case "--tui":
                    config.Transport.EnableTui = true;
                    break;

                case "--enable-port-mapping":
                case "--enable-upnp":
                    config.EnablePortMapping = true;
                    break;

                case "--external-port":
                case "-ep":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int ep))
                        config.ExternalPort = ep;
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static PeerConfig? LoadConfig(string[] args)
    {
        return JsonConfigFile.Load(
            args,
            IntranetConfigJsonContext.Default.PeerConfig,
            ConfigJsonCompatibility.NormalizeIntranet,
            "--config",
            "-c");
    }

    private static bool TryParseTarget(string value, out TargetEndpoint target)
    {
        target = new TargetEndpoint();
        if (!TryParseOptionalPort(value, out var host, out var port) || string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        target = new TargetEndpoint
        {
            Host = host,
            Port = port
        };
        return true;
    }

    private static void AddTargets(string value, PeerConfig config)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseTarget(item, out var target))
            {
                config.AllowedTargets.Add(target);
            }
        }
    }

    private static bool TryParseSubnet(string value, out AllowedSubnet subnet)
    {
        subnet = new AllowedSubnet();
        if (!TryParseOptionalPort(value, out var cidr, out var port))
        {
            return false;
        }

        var parts = cidr.Split('/', 2);
        if (parts.Length != 2 || !int.TryParse(parts[1], out var prefixLength) || prefixLength < 0 || prefixLength > 32)
        {
            return false;
        }

        subnet = new AllowedSubnet
        {
            Cidr = cidr,
            Port = port
        };
        return true;
    }

    private static bool TryParseOptionalPort(string value, out string hostOrCidr, out int? port)
    {
        hostOrCidr = value;
        port = null;

        var colonIndex = value.LastIndexOf(':');
        if (colonIndex < 0)
        {
            return !string.IsNullOrWhiteSpace(hostOrCidr);
        }

        if (colonIndex == 0 || colonIndex == value.Length - 1)
        {
            return false;
        }

        hostOrCidr = value[..colonIndex];
        var portText = value[(colonIndex + 1)..];
        if (portText is "*" or "any")
        {
            return !string.IsNullOrWhiteSpace(hostOrCidr);
        }

        if (!int.TryParse(portText, out var parsedPort) || parsedPort <= 0 || parsedPort > 65535)
        {
            return false;
        }

        port = parsedPort;
        return !string.IsNullOrWhiteSpace(hostOrCidr);
    }

    private static void AddSubnets(string value, PeerConfig config)
    {
        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseSubnet(item, out var subnet))
            {
                config.AllowedSubnets.Add(subnet);
            }
        }
    }

    private static void EnsureDefaultAllowedTarget(PeerConfig config)
    {
        if (config.AllowedTargets.Count > 0)
        {
            return;
        }

        config.AllowedTargets.Add(new TargetEndpoint
        {
            Host = config.TargetSourceHost,
            Port = config.TargetSourcePort
        });
    }
    
    private static void PrintHelp()
    {
        Console.WriteLine("Usage: LanBridge.IntranetPeer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --node-id, -id <id>             Node identifier (default: intranet-peer-001)");
        Console.WriteLine("  --signaling-host, -sh <host>    Signaling server host (default: 127.0.0.1)");
        Console.WriteLine("  --signaling-port, -sp <port>    Signaling server port (default: 9000)");
        Console.WriteLine("  --stun-host, -sth <host>        STUN server host (default: 127.0.0.1)");
        Console.WriteLine("  --stun-port, -stp <port>        STUN server port (default: 9001)");
        Console.WriteLine("  --stun-alt-port <port>          Alternate STUN port for NAT detection (default: 9003)");
        Console.WriteLine("  --config, -c <path>             Load JSON config file");
        Console.WriteLine("  --token, -t <token>             Authentication token (default: default-token)");
        Console.WriteLine("  --target-host, -th <host>       Target source host (default: 127.0.0.1)");
        Console.WriteLine("  --target-port, -tp <port>       Target source port (default: 554)");
        Console.WriteLine("  --allow-target, -at <host[:port|:*]> Allow endpoint; omit port to allow any TCP port");
        Console.WriteLine("  --allow-targets <list>          Allow comma-separated endpoints");
        Console.WriteLine("  --allow-subnet <cidr[:port|:*]> Allow subnet, e.g. 192.168.7.0/24 or 192.168.7.0/24:554");
        Console.WriteLine("  --allow-subnets <list>          Allow comma-separated subnets");
        Console.WriteLine("  --udp-port, -up <port>          UDP port for P2P (default: random)");
        Console.WriteLine("  --signaling-transport <tcp|ws|auto> Signaling transport (default: tcp)");
        Console.WriteLine("  --ws-port <port>                WebSocket signaling port (default: 9010)");
        Console.WriteLine("  --enable-port-mapping, --enable-upnp Enable UPnP/NAT-PMP port mapping");
        Console.WriteLine("  --external-port, -ep <port>     Desired external port for mapping (default: 0 / auto)");
        Console.WriteLine("  --verbose, -v                   Enable detailed KCP diagnostics");
        Console.WriteLine("  --tui                           Enable real-time TUI dashboard");
        Console.WriteLine("  --help, -h                      Show this help");
    }

    private static string ReadValidatedSignalingTransport(string[] args, ref int index, string optionName)
    {
        var value = ReadRequiredValue(args, ref index, optionName);
        ConfigValidation.EnsureSupportedSignalingTransport(value, optionName);
        return value.ToLowerInvariant();
    }

    private static int ReadIntArgument(string[] args, ref int index, string optionName)
    {
        var value = ReadRequiredValue(args, ref index, optionName);
        if (!int.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"{optionName} must be an integer.");
        }

        return parsed;
    }

    private static string ReadRequiredValue(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{optionName} requires a value.");
        }

        return args[++index];
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[System.Text.Json.Serialization.JsonSerializable(typeof(PeerConfig))]
internal partial class IntranetConfigJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
