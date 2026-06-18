using System.Net;
using System.Net.Sockets;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Protocol;
using LanBridge.Common.Runtime;

namespace LanBridge.SignalingServer;

public class StunService : IDisposable
{
    private readonly UdpClient _primaryClient;
    private readonly UdpClient? _alternateClient;
    private readonly int _primaryPort;
    private readonly int _alternatePort;
    private readonly OperationalTelemetry _telemetry;
    private bool _isRunning;

    public event Action<string, IPEndPoint>? OnStunRequest;

    public StunService(int primaryPort, int alternatePort, OperationalTelemetry telemetry)
    {
        _primaryPort = primaryPort;
        _alternatePort = alternatePort;
        _telemetry = telemetry;
        _primaryClient = CreateDualStackUdpClient(primaryPort);
        if (alternatePort > 0 && alternatePort != primaryPort)
        {
            _alternateClient = CreateDualStackUdpClient(alternatePort);
        }
    }

    public async Task StartAsync()
    {
        _isRunning = true;
        ConsoleStatusWriter.WriteServerStatus("STUN", $"Listening on UDP port {_primaryPort} (dual-stack)", ConsoleColor.DarkCyan);
        if (_alternateClient != null)
        {
            ConsoleStatusWriter.WriteServerStatus("STUN", $"Alternate listening on UDP port {_alternatePort} (dual-stack)", ConsoleColor.DarkCyan);
        }

        var primaryLoop = ReceiveLoopAsync(_primaryClient, _alternateClient);
        var alternateLoop = _alternateClient == null
            ? Task.CompletedTask
            : ReceiveLoopAsync(_alternateClient, _primaryClient);
        await Task.WhenAll(primaryLoop, alternateLoop);
    }

    private async Task ReceiveLoopAsync(UdpClient receiveClient, UdpClient? changePortClient)
    {
        while (_isRunning)
        {
            try
            {
                var result = await receiveClient.ReceiveAsync();
                _ = Task.Run(() => HandleStunRequestAsync(result, receiveClient, changePortClient));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleStatusWriter.WriteServerStatus("STUN", $"Receive error: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private async Task HandleStunRequestAsync(UdpReceiveResult result, UdpClient receiveClient, UdpClient? changePortClient)
    {
        try
        {
            if (!StunProtocol.TryParseBindingRequest(result.Buffer, out var standardRequest))
            {
                return;
            }

            var responseClient = standardRequest.ChangePort && changePortClient != null
                ? changePortClient
                : receiveClient;
            var response = StunProtocol.CreateBindingSuccessResponse(standardRequest.TransactionId, result.RemoteEndPoint);
            await responseClient.SendAsync(response, response.Length, result.RemoteEndPoint);
            _telemetry.Increment("stun_requests");
            ConsoleStatusWriter.WriteServerStatus(
                "STUN",
                $"Binding response to {result.RemoteEndPoint}" +
                (standardRequest.ChangePort && changePortClient != null ? " from alternate port" : string.Empty),
                ConsoleColor.DarkCyan);
            OnStunRequest?.Invoke("STUN_BINDING_REQUEST", result.RemoteEndPoint);
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("STUN", $"Handle error: {ex.Message}", ConsoleColor.Red);
        }
    }

    private static UdpClient CreateDualStackUdpClient(int port)
    {
        try
        {
            var client = new UdpClient(AddressFamily.InterNetworkV6);
            client.Client.DualMode = true;
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return client;
        }
        catch
        {
            return new UdpClient(port);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _primaryClient.Dispose();
        _alternateClient?.Dispose();
    }
}
