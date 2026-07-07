using LanBridge.Common.Configuration;

namespace LanBridge.IntranetPeer;

/// <summary>
/// 内网代理节点主程序
/// </summary>
public class Program
{
    private static readonly ConsoleStatusWriter s_statusWriter = new();

    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== LanBridge Intranet Peer ===");
        Console.WriteLine();

        var config = ConfigLoader.LoadConfig(args, ConfigJsonContext.Default.PeerConfig) ?? new PeerConfig();
        ParseArguments(args, config);
        EnsureDefaultAllowedTarget(config);

        var errors = config.Validate();
        if (errors.Count > 0)
        {
            foreach (var e in errors)
                Console.WriteLine($"Config error: {e}");
            return;
        }

        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  Node ID: {config.NodeId}");
        Console.WriteLine($"  Signaling Server: {config.SignalingServerHost}:{config.SignalingServerPort}");
        Console.WriteLine($"  STUN Server: {config.StunServerHost}:{config.StunServerPort}");
        Console.WriteLine($"  STUN Alternate Port: {config.StunAlternateServerPort}");
        Console.WriteLine($"  Target Source: {config.TargetSourceHost}:{config.TargetSourcePort}");
        Console.WriteLine($"  Allowed Targets: {string.Join(", ", config.AllowedTargets)}");
        Console.WriteLine($"  Allowed Subnets: {string.Join(", ", config.AllowedSubnets)}");
        Console.WriteLine($"  Verbose: {(config.Verbose ? "enabled" : "disabled")}");
        Console.WriteLine();

        using var peer = new IntranetPeer(config);

        peer.OnStatusChanged += status =>
        {
            s_statusWriter.WriteStatus(status);
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
                    if (i + 1 < args.Length && EndpointParser.TryParseTarget(args[++i], out var target))
                        config.AllowedTargets.Add(target);
                    break;

                case "--allow-targets":
                    if (i + 1 < args.Length)
                        EndpointParser.AddTargets(args[++i], config.AllowedTargets);
                    break;

                case "--allow-subnet":
                    if (i + 1 < args.Length && CidrParser.TryParse(args[++i], out var subnet))
                        config.AllowedSubnets.Add(subnet);
                    break;

                case "--allow-subnets":
                    if (i + 1 < args.Length)
                        CidrParser.AddSubnets(args[++i], config.AllowedSubnets);
                    break;

                case "--udp-port":
                case "-up":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int up))
                        config.UdpPort = up;
                    break;

                case "--verbose":
                case "-v":
                    config.Verbose = true;
                    break;

                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
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
        Console.WriteLine("  --verbose, -v                   Enable detailed KCP diagnostics");
        Console.WriteLine("  --help, -h                      Show this help");
    }
}