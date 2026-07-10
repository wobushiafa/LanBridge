using LanBridge.Common.Configuration;
using LanBridge.Common.Protocol;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Runtime;

namespace LanBridge.SignalingServer;

/// <summary>
/// 信令服务器主程序
/// </summary>
public class Program
{
    private static ServerConfig _config = new();
    private static StunService? _stunService;
    private static SignalingService? _signalingService;
    private static WebSocketSignalingService? _wsSignalingService;
    private static RelayService? _relayService;
    private static readonly OperationalTelemetry Telemetry = new();
    
    public static async Task Main(string[] args)
    {
        ConsoleStatusWriter.WriteHeader("LanBridge Signaling Server");
        
        _config = LoadConfig(args) ?? new ServerConfig();
        ParseArguments(args);
        _config.Validate();
        
        ConsoleStatusWriter.WriteConfiguration(new[]
        {
            ("Signaling Port", _config.Ports.SignalingPort.ToString()),
            ("STUN Port", _config.Ports.StunPort.ToString()),
            ("STUN Alternate Port", _config.Ports.StunAlternatePort.ToString()),
            ("Relay Port", _config.Ports.RelayPort.ToString()),
            ("WebSocket Port", _config.Ports.WebSocketPort == 0 ? "disabled" : _config.Ports.WebSocketPort.ToString()),
            ("Max Relay Sessions", _config.Relay.MaxSessions.ToString()),
            ("Relay Timeout", $"{_config.Relay.IdleTimeoutMs}ms"),
            ("Require Registration Token", _config.Security.RequireRegistrationToken ? "enabled" : "disabled"),
            ("Metrics Interval", $"{_config.Metrics.ReportIntervalSeconds}s")
        });
        
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
            _stunService = new StunService(_config.StunPort, _config.StunAlternatePort, Telemetry);
            _stunService.OnStunRequest += (msg, ep) =>
            {
                ConsoleStatusWriter.WriteServerStatus("STUN", $"Request from {ep}", ConsoleColor.DarkCyan);
            };
            
            // 启动信令服务
            _signalingService = new SignalingService(_config, Telemetry);
            _signalingService.OnMessageReceived += (clientId, message) =>
            {
                ConsoleStatusWriter.WriteServerStatus("Signaling", $"Message from {clientId}: {message.Type}", ConsoleColor.DarkGray);
            };

            // 启动中转服务
            _relayService = new RelayService(_config.RelayPort, _config.MaxRelaySessions, _config.RelayTimeoutMs, Telemetry);

            // 并行启动所有服务
            var tasks = new List<Task>
            {
                Task.Run(() => _stunService.StartAsync()),
                Task.Run(() => _signalingService.StartAsync()),
                Task.Run(() => _relayService.StartAsync())
            };

            if (_config.WebSocketPort > 0)
            {
                _wsSignalingService = new WebSocketSignalingService(_config.WebSocketPort, _signalingService);
                tasks.Add(Task.Run(() => _wsSignalingService.StartAsync(cts.Token)));
            }
            var metricsTask = Task.Run(() => ReportMetricsLoopAsync(cts.Token));
            
            ConsoleStatusWriter.WriteServerStatus("Server", "All services started. Press Ctrl+C to shutdown.", ConsoleColor.Green);
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
            ConsoleStatusWriter.WriteServerStatus("Server", "Shutting down services...", ConsoleColor.Yellow);
            _stunService?.Dispose();
            _wsSignalingService?.Dispose();
            _signalingService?.Dispose();
            _relayService?.Dispose();
            ConsoleStatusWriter.WriteServerStatus("Server", "Server stopped.", ConsoleColor.Yellow);
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

                case "--ws-port":
                case "-wsp":
                    _config.WebSocketPort = ReadIntArgument(args, ref i, args[i]);
                    break;

                case "--relay-timeout":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int relayTimeout))
                        _config.RelayTimeoutMs = relayTimeout;
                    break;

                case "--require-token":
                    _config.RequireRegistrationToken = true;
                    break;

                case "--registration-token":
                    if (i + 1 < args.Length)
                        _config.RegistrationTokens.Add(args[++i]);
                    break;

                case "--metrics-interval":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int metricsInterval))
                        _config.MetricsReportIntervalSeconds = metricsInterval;
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
        return JsonConfigFile.Load(
            args,
            ServerConfigJsonContext.Default.ServerConfig,
            ConfigJsonCompatibility.NormalizeServer,
            "--config",
            "-c");
    }

    private static async Task ReportMetricsLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(_config.MetricsReportIntervalSeconds));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            ConsoleStatusWriter.WriteServerStatus("Metrics", Telemetry.FormatSnapshot(), ConsoleColor.DarkGray);
        }
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
        Console.WriteLine("  --ws-port, -wsp <port>         WebSocket signaling port (default: disabled; e.g. 9010 to enable)");
        Console.WriteLine("  --relay-timeout <ms>           Relay idle timeout in ms (default: 30000)");
        Console.WriteLine("  --require-token                Require registration token for intranet node registration");
        Console.WriteLine("  --registration-token <token>   Add an allowed registration token (repeatable)");
        Console.WriteLine("  --metrics-interval <sec>       Metrics reporting interval in seconds (default: 30)");
        Console.WriteLine("  --config, -c <path>            Load JSON config file");
        Console.WriteLine("  --help, -h                     Show this help");
    }

    private static int ReadIntArgument(string[] args, ref int index, string optionName)
    {
        if (index + 1 >= args.Length)
        {
            throw new InvalidOperationException($"{optionName} requires a value.");
        }

        var value = args[++index];
        if (!int.TryParse(value, out var parsed))
        {
            throw new InvalidOperationException($"{optionName} must be an integer.");
        }

        return parsed;
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
