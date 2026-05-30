using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge.Common.Network;

public sealed class LanDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 9005;
    private const string MulticastGroup = "239.255.0.1"; // Standard site-local multicast IP
    
    private readonly UdpClient _udpClient;
    private readonly string _nodeId;
    private readonly ConnectionNegotiator _negotiator;
    private readonly bool _verbose;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;

    public LanDiscoveryService(string nodeId, ConnectionNegotiator negotiator, bool verbose = false)
    {
        _nodeId = nodeId;
        _negotiator = negotiator;
        _verbose = verbose;

        _udpClient = new UdpClient();
        _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        
        try
        {
            _udpClient.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));
        }
        catch
        {
            // Multicast joining might fail if local interface lacks multicast support, fallback to broadcast only
        }
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _listenTask = Task.Run(ListenLoopAsync, _cts.Token);
    }

    private async Task ListenLoopAsync()
    {
        var token = _cts!.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(token);
                string message = Encoding.UTF8.GetString(result.Buffer);
                
                // Protocol: LB_DISCOVER:<TargetNodeId>:<ClientNodeId>:<ClientLocalUdpPort>:<Conv>
                if (message.StartsWith("LB_DISCOVER:", StringComparison.Ordinal))
                {
                    var parts = message.Split(':');
                    if (parts.Length == 5)
                    {
                        var targetNodeId = parts[1];
                        var clientNodeId = parts[2];
                        if (targetNodeId == _nodeId && int.TryParse(parts[3], out var clientPort) && uint.TryParse(parts[4], out var conv))
                        {
                            var clientIp = result.RemoteEndPoint.Address;
                            var clientEndPoint = new IPEndPoint(clientIp, clientPort);
                            
                            // Trigger the negotiator to reply with a unicast LB_ADVERTISE and setup direct LAN session
                            await _negotiator.HandleLanDiscoveryRequestAsync(clientEndPoint, conv);
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                if (_verbose)
                {
                    _negotiator.RaiseStatusChanged($"[LAN Discovery] Receive error: {ex.Message}");
                }
            }
        }
    }

    public async Task BroadcastQueryAsync(string targetNodeId, int clientPort, uint conv)
    {
        try
        {
            var message = $"LB_DISCOVER:{targetNodeId}:{_nodeId}:{clientPort}:{conv}";
            var data = Encoding.UTF8.GetBytes(message);
            
            // 1. Send site-local multicast
            await _udpClient.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Parse(MulticastGroup), DiscoveryPort));
            
            // 2. Send subnet-wide broadcast
            await _udpClient.SendAsync(data, data.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            
            if (_verbose)
            {
                _negotiator.RaiseStatusChanged($"[LAN Discovery] Sent query for target={targetNodeId}, clientPort={clientPort}, conv={conv}");
            }
        }
        catch (Exception ex)
        {
            if (_verbose)
            {
                _negotiator.RaiseStatusChanged($"[LAN Discovery] Broadcast query failed: {ex.Message}");
            }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _udpClient.Dispose();
    }
}
