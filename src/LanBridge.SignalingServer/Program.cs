using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

/// <summary>
/// 信令服务器主程序
/// </summary>
public class Program
{
    private static ServerConfig _config = new();
    private static StunService? _stunService;
    private static SignalingService? _signalingService;
    private static RelayService? _relayService;
    
    public static async Task Main(string[] args)
    {
        Console.WriteLine("=== LanBridge Signaling Server ===");
        Console.WriteLine();
        
        _config = LoadConfig(args) ?? new ServerConfig();
        ParseArguments(args);
        
        Console.WriteLine($"Configuration:");
        Console.WriteLine($"  Signaling Port: {_config.SignalingPort}");
        Console.WriteLine($"  STUN Port: {_config.StunPort}");
        Console.WriteLine($"  STUN Alternate Port: {_config.StunAlternatePort}");
        Console.WriteLine($"  Relay Port: {_config.RelayPort}");
        Console.WriteLine($"  Max Relay Sessions: {_config.MaxRelaySessions}");
        Console.WriteLine();
        
        // 启动服务
        using var cts = new CancellationTokenSource();
        
        Console.CancelKeyPress += (s, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("\nShutting down...");
            cts.Cancel();
        };
        
        try
        {
            // 启动 STUN 服务
            _stunService = new StunService(_config.StunPort, _config.StunAlternatePort);
            _stunService.OnStunRequest += (msg, ep) =>
            {
                Console.WriteLine($"[STUN] Request from {ep}");
            };
            
            // 启动信令服务
            _signalingService = new SignalingService(_config.SignalingPort, _config.RelayPort);
            _signalingService.OnMessageReceived += (clientId, message) =>
            {
                Console.WriteLine($"[Signaling] Message from {clientId}: {message.Type}");
            };
            
            // 启动中转服务
            _relayService = new RelayService(_config.RelayPort, _config.MaxRelaySessions);
            
            // 并行启动所有服务
            var tasks = new[]
            {
                Task.Run(() => _stunService.StartAsync()),
                Task.Run(() => _signalingService.StartAsync()),
                Task.Run(() => _relayService.StartAsync())
            };
            
            Console.WriteLine("All services started. Press Ctrl+C to shutdown.");
            Console.WriteLine();
            
            // 等待取消信号
            try
            {
                await Task.Delay(Timeout.Infinite, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // 正常关闭
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
        finally
        {
            Console.WriteLine("Shutting down services...");
            _stunService?.Dispose();
            _signalingService?.Dispose();
            _relayService?.Dispose();
            Console.WriteLine("Server stopped.");
        }
    }
    
    private static void ParseArguments(string[] args)
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

                case "--signaling-port":
                case "-sp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int sp))
                        _config.SignalingPort = sp;
                    break;
                
                case "--stun-port":
                case "-stun":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int stunPort))
                        _config.StunPort = stunPort;
                    break;

                case "--stun-alt-port":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int stunAltPort))
                        _config.StunAlternatePort = stunAltPort;
                    break;
                
                case "--relay-port":
                case "-rp":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int rp))
                        _config.RelayPort = rp;
                    break;
                
                case "--help":
                case "-h":
                    PrintHelp();
                    Environment.Exit(0);
                    break;
            }
        }
    }

    private static ServerConfig? LoadConfig(string[] args)
    {
        var configPath = FindOptionValue(args, "--config", "-c");
        if (string.IsNullOrWhiteSpace(configPath))
        {
            return null;
        }

        var json = File.ReadAllText(configPath);
        return System.Text.Json.JsonSerializer.Deserialize(json, ServerConfigJsonContext.Default.ServerConfig);
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
    
    private static void PrintHelp()
    {
        Console.WriteLine("Usage: LanBridge.SignalingServer [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --signaling-port, -sp <port>   Signaling server port (default: 9000)");
        Console.WriteLine("  --stun-port, -stun <port>      STUN server port (default: 9001)");
        Console.WriteLine("  --stun-alt-port <port>         Alternate STUN port for NAT detection (default: 9003)");
        Console.WriteLine("  --relay-port, -rp <port>       Relay server port (default: 9002)");
        Console.WriteLine("  --config, -c <path>            Load JSON config file");
        Console.WriteLine("  --help, -h                     Show this help");
    }
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[System.Text.Json.Serialization.JsonSerializable(typeof(ServerConfig))]
internal partial class ServerConfigJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
