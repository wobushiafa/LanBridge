namespace LanBridge.ExtranetPeer;

/// <summary>
/// 外网客户端节点主程序
/// </summary>
public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== LanBridge Extranet Client ===");
        Console.WriteLine();
        
        var config = LoadConfig(args) ?? new ClientConfig();
        ParseArguments(args, config);
        EnsureDefaultMapping(config);
        
        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  Node ID: {config.NodeId}");
        Console.WriteLine($"  Signaling Server: {config.SignalingServerHost}:{config.SignalingServerPort}");
        Console.WriteLine($"  STUN Server: {config.StunServerHost}:{config.StunServerPort}");
        Console.WriteLine($"  STUN Alternate Port: {config.StunAlternateServerPort}");
        Console.WriteLine($"  Target Node: {config.TargetNodeId}");
        Console.WriteLine($"  Local Proxy Port: {config.LocalProxyPort}");
        foreach (var mapping in config.Mappings)
        {
            Console.WriteLine($"  Mapping: 127.0.0.1:{mapping.LocalPort} -> {(string.IsNullOrWhiteSpace(mapping.Target) ? "intranet default target" : mapping.Target)}");
        }
        Console.WriteLine($"  Hole Punch Timeout: {config.HolePunchTimeoutMs}ms");
        Console.WriteLine($"  Relay Fallback: {(config.EnableRelayFallback ? "enabled" : "disabled")}");
        Console.WriteLine($"  Verbose: {(config.Verbose ? "enabled" : "disabled")}");
        Console.WriteLine();
        
        using var peer = new ExtranetPeer(config);
        
        peer.OnStatusChanged += status =>
        {
            WriteStatus(status);
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
                WriteColored($"[{DateTime.Now:HH:mm:ss}] Traffic: {remoteBytes / 1024.0:F1} KB from remote via {mode}", ConsoleColor.DarkGray);
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

    private static void WriteStatus(string status)
    {
        if (status.Contains("P2P connection established", StringComparison.OrdinalIgnoreCase))
        {
            WriteModeBanner("P2P DIRECT", ConsoleColor.Green);
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Green);
            return;
        }

        if (status.Contains("Relay connection established", StringComparison.OrdinalIgnoreCase))
        {
            WriteModeBanner("RELAY MODE", ConsoleColor.Yellow);
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Yellow);
            return;
        }

        if (status.Contains("relay", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Yellow);
            return;
        }

        if (status.Contains("error", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("timeout", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Red);
            return;
        }

        if (status.StartsWith("State:", StringComparison.OrdinalIgnoreCase) ||
            status.Contains("Hole punch", StringComparison.OrdinalIgnoreCase))
        {
            WriteColored($"[{DateTime.Now:HH:mm:ss}] {status}", ConsoleColor.Cyan);
            return;
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {status}");
    }

    private static void WriteModeBanner(string mode, ConsoleColor color)
    {
        WriteColored("", color);
        WriteColored("============================================================", color);
        WriteColored($"  TRANSPORT MODE: {mode}", color);
        WriteColored("============================================================", color);
    }

    private static void WriteColored(string message, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(message);
        Console.ForegroundColor = originalColor;
    }
    
    private static void ParseArguments(string[] args, ClientConfig config)
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
                        config.Mappings.Add(mapping);
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

    private static ClientConfig? LoadConfig(string[] args)
    {
        var configPath = FindOptionValue(args, "--config", "-c");
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        var json = File.ReadAllText(configPath);
        return System.Text.Json.JsonSerializer.Deserialize(json, ExtranetConfigJsonContext.Default.ClientConfig);
    }

    private static string? FindOptionValue(string[] args, string longName, string shortName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == longName || args[i] == shortName)
            {
                return args[i + 1];
            }
        }

        return null;
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
        var parts = targetPart.Split(':');
        if (parts.Length < 2)
        {
            return false;
        }

        string protocol = "tcp";
        int targetPort;
        string targetHost;

        var lastPart = parts[^1];
        if (string.Equals(lastPart, "udp", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(lastPart, "tcp", StringComparison.OrdinalIgnoreCase))
        {
            protocol = lastPart.ToLowerInvariant();
            if (parts.Length < 3 || !int.TryParse(parts[^2], out targetPort))
            {
                return false;
            }
            targetHost = string.Join(":", parts[..^2]);
        }
        else
        {
            if (!int.TryParse(lastPart, out targetPort))
            {
                return false;
            }
            targetHost = string.Join(":", parts[..^1]);
        }

        if (string.IsNullOrWhiteSpace(targetHost))
        {
            return false;
        }

        mapping = new TunnelMapping
        {
            LocalPort = localPort,
            TargetHost = targetHost,
            TargetPort = targetPort,
            Protocol = protocol
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
        Console.WriteLine("  --udp-port, -up <port>          UDP port for P2P (default: random)");
        Console.WriteLine("  --punch-timeout, -pt <ms>       Hole punch timeout in ms (default: 10000)");
        Console.WriteLine("  --no-relay                      Disable relay fallback");
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
