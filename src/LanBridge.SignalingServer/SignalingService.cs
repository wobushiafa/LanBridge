using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Diagnostics;
using LanBridge.Common.Protocol;
using LanBridge.Common.Runtime;

namespace LanBridge.SignalingServer;

public class SignalingService : IDisposable
{
    private readonly TcpListener _listener;
    private readonly ServerConfig _config;
    private readonly OperationalTelemetry _telemetry;
    private readonly ConcurrentDictionary<string, ISignalingConnection> _connections = new();
    private readonly ConcurrentDictionary<string, NodeInfo> _nodes = new();
    private readonly ConcurrentDictionary<string, string> _clientToNode = new();
    private bool _isRunning;

    public event Action<string, BaseMessage>? OnMessageReceived;

    /// <summary>
    /// Actual TCP port the listener bound to. Differs from
    /// <see cref="ServerConfig.SignalingPort"/> when that was configured as 0
    /// (OS-assigned ephemeral port). Returns 0 before <see cref="StartAsync"/>.
    /// </summary>
    public int ActualPort => (_listener.LocalEndpoint as IPEndPoint)?.Port ?? 0;

    /// <summary>
    /// Registers a connection (any transport) keyed by clientId. Must be called with
    /// the same clientId used for message processing. The TCP accept loop registers
    /// a <see cref="TcpSignalingConnection"/>; non-TCP transports (WebSocket) register
    /// a <see cref="BridgeSignalingConnection"/> here.
    /// </summary>
    public void RegisterConnection(string clientId, ISignalingConnection conn)
        => _connections[clientId] = conn;

    /// <summary>
    /// Removes a connection registration and clears its node binding. Called by
    /// non-TCP transports on disconnect; mirrors the cleanup performed by the TCP
    /// client handler's finally block.
    /// </summary>
    public void UnregisterConnection(string clientId)
    {
        _connections.TryRemove(clientId, out _);
        RemoveNodeBinding(clientId);
    }

    /// <summary>
    /// Clears the clientId→nodeId binding (if any) and reports the disconnect.
    /// Idempotent: the <c>TryRemove</c> guards ensure single-fire telemetry even when
    /// both <see cref="DisconnectClientAsync"/> (reject path) and the receive loop's
    /// finally run. Called by <see cref="UnregisterConnection"/> and the TCP finally.
    /// </summary>
    private void RemoveNodeBinding(string clientId)
    {
        if (_clientToNode.TryRemove(clientId, out var nodeId))
        {
            _nodes.TryRemove(nodeId, out _);
            _telemetry.Increment("signaling_nodes_unregistered");
            ConsoleStatusWriter.WriteServerStatus("Signaling", $"Node unregistered: {nodeId}", ConsoleColor.DarkYellow);
        }

        _telemetry.Increment("signaling_clients_disconnected");
        ConsoleStatusWriter.WriteServerStatus("Signaling", $"Client disconnected: {clientId}", ConsoleColor.Gray);
    }

    /// <summary>
    /// Process a message from any transport (TCP or WebSocket).
    /// Public so WebSocketSignalingService can delegate to the same logic.
    /// </summary>
    public async Task ProcessMessageFromTransportAsync(string clientId, BaseMessage message)
    {
        await ProcessMessageAsync(clientId, message);
    }

    public SignalingService(ServerConfig config, OperationalTelemetry telemetry)
    {
        _config = config;
        _telemetry = telemetry;
        _listener = new TcpListener(IPAddress.Any, config.SignalingPort);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;
        ConsoleStatusWriter.WriteServerStatus("Signaling", $"Listening on TCP port {_config.SignalingPort}", ConsoleColor.Gray);

        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                var clientId = Guid.NewGuid().ToString("N")[..8];
                var conn = new TcpSignalingConnection(client);
                _connections[clientId] = conn;
                _telemetry.Increment("signaling_clients_connected");
                ConsoleStatusWriter.WriteServerStatus("Signaling", $"Client connected: {clientId}", ConsoleColor.Gray);
                _ = Task.Run(() => HandleClientAsync(clientId, conn));
            }
            catch (Exception) when (!_isRunning)
            {
                break;
            }
            catch (Exception ex)
            {
                ConsoleStatusWriter.WriteServerStatus("Signaling", $"Accept error: {ex.Message}", ConsoleColor.Red);
            }
        }
    }

    private async Task HandleClientAsync(string clientId, TcpSignalingConnection conn)
    {
        var buffer = new byte[65536];
        var stream = conn.Stream;

        try
        {
            while (conn.Connected && _isRunning)
            {
                var lengthBuffer = new byte[4];
                int bytesRead = 0;
                while (bytesRead < 4)
                {
                    var read = await stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
                    if (read == 0)
                    {
                        throw new IOException("Connection closed");
                    }

                    bytesRead += read;
                }

                var messageLength = BitConverter.ToInt32(lengthBuffer, 0);
                if (messageLength <= 0 || messageLength > buffer.Length)
                {
                    throw new InvalidDataException($"Invalid message length: {messageLength}");
                }

                bytesRead = 0;
                while (bytesRead < messageLength)
                {
                    var read = await stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                    {
                        throw new IOException("Connection closed");
                    }

                    bytesRead += read;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, messageLength);
                var baseMessage = MessageSerializer.Deserialize(message);
                if (baseMessage == null)
                {
                    continue;
                }

                if (_clientToNode.TryGetValue(clientId, out var knownNodeId) && _nodes.TryGetValue(knownNodeId, out var nodeInfo))
                {
                    nodeInfo.LastSeen = DateTime.UtcNow;
                }

                OnMessageReceived?.Invoke(clientId, baseMessage);
                await ProcessMessageAsync(clientId, baseMessage);
            }
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("Signaling", $"Client {clientId} error: {ex.Message}", ConsoleColor.Red);
        }
        finally
        {
            _connections.TryRemove(clientId, out _);
            conn.Dispose();
            RemoveNodeBinding(clientId);
        }
    }

    private async Task ProcessMessageAsync(string clientId, BaseMessage message)
    {
        switch (message.Type)
        {
            case MessageType.Register:
                await HandleRegisterAsync(clientId, (RegisterMessage)message);
                break;
            case MessageType.ConnectRequest:
                await HandleConnectRequestAsync(clientId, (ConnectRequest)message);
                break;
            case MessageType.HolePunchReady:
                await HandleHolePunchReadyAsync(clientId, (HolePunchStart)message);
                break;
            case MessageType.RelayRequest:
                await HandleRelayRequestAsync(clientId, (RelayRequest)message);
                break;
        }
    }

    private async Task HandleRegisterAsync(string clientId, RegisterMessage message)
    {
        if (!ValidateRegistrationToken(message.Token))
        {
            _telemetry.Increment("registration_rejected");
            var ack = new RegisterAck
            {
                Success = false,
                Message = "Registration token rejected"
            };
            await SendToClientAsync(clientId, ack);
            ConsoleStatusWriter.WriteServerStatus("Signaling", $"Rejected node registration for {message.NodeId}", ConsoleColor.Red);
            await DisconnectClientAsync(clientId);
            return;
        }

        IPEndPoint? publicEndPoint = null;
        if (!string.IsNullOrWhiteSpace(message.PublicEndPoint) &&
            IPEndPoint.TryParse(message.PublicEndPoint, out var registeredEndPoint))
        {
            publicEndPoint = registeredEndPoint;
        }

        IPEndPoint? publicEndPointV6 = null;
        if (!string.IsNullOrWhiteSpace(message.PublicEndPointV6) &&
            IPEndPoint.TryParse(message.PublicEndPointV6, out var registeredEndPointV6))
        {
            publicEndPointV6 = registeredEndPointV6;
        }

        var nodeInfo = new NodeInfo
        {
            NodeId = message.NodeId,
            ClientId = clientId,
            PublicEndPoint = publicEndPoint,
            PublicEndPointV6 = publicEndPointV6,
            LastSeen = DateTime.UtcNow,
            IsIntranet = true,
            NatType = message.NatType
        };

        _nodes[message.NodeId] = nodeInfo;
        _clientToNode[clientId] = message.NodeId;
        _telemetry.Increment("registration_success");

        await SendToClientAsync(clientId, new RegisterAck
        {
            Success = true,
            Message = "Registered successfully"
        });

        ConsoleStatusWriter.WriteServerStatus(
            "Signaling",
            $"Node registered: {message.NodeId} (IPv4={publicEndPoint}, IPv6={publicEndPointV6}, NatType={message.NatType})",
            ConsoleColor.DarkGreen);
    }

    private async Task HandleConnectRequestAsync(string clientId, ConnectRequest message)
    {
        _telemetry.Increment("connect_requests");
        if (!_nodes.TryGetValue(message.TargetNodeId, out var targetNode))
        {
            await SendToClientAsync(clientId, new ErrorMessage
            {
                Code = 404,
                Message = $"Target node {message.TargetNodeId} not found"
            });
            return;
        }

        IPEndPoint? requestEndPoint = null;
        if (!string.IsNullOrWhiteSpace(message.ClientEndPoint) &&
            IPEndPoint.TryParse(message.ClientEndPoint, out var parsedRequestEndPoint))
        {
            requestEndPoint = parsedRequestEndPoint;
        }

        IPEndPoint? requestEndPointV6 = null;
        if (!string.IsNullOrWhiteSpace(message.ClientEndPointV6) &&
            IPEndPoint.TryParse(message.ClientEndPointV6, out var parsedRequestEndPointV6))
        {
            requestEndPointV6 = parsedRequestEndPointV6;
        }

        if ((targetNode.PublicEndPoint == null && targetNode.PublicEndPointV6 == null) ||
            (requestEndPoint == null && requestEndPointV6 == null))
        {
            await SendToClientAsync(clientId, new ErrorMessage
            {
                Code = 426,
                Message = "UDP hole punch endpoint unavailable; relay required"
            });
            return;
        }

        var sessionId = Guid.NewGuid().ToString("N")[..8];
        var conv = (uint)Random.Shared.Next(1, int.MaxValue);

        await SendToClientAsync(targetNode.ClientId, new HolePunchStart
        {
            SessionId = sessionId,
            TargetEndPoint = requestEndPoint?.ToString() ?? string.Empty,
            TargetEndPointV6 = requestEndPointV6?.ToString(),
            IsInitiator = true,
            Conv = conv,
            TargetNatType = message.NatType
        });

        await SendToClientAsync(clientId, new ConnectReady
        {
            SessionId = sessionId,
            IntranetEndPoint = targetNode.PublicEndPoint?.ToString() ?? string.Empty,
            IntranetEndPointV6 = targetNode.PublicEndPointV6?.ToString(),
            ExtranetEndPoint = requestEndPoint?.ToString() ?? string.Empty,
            ExtranetEndPointV6 = requestEndPointV6?.ToString(),
            RelayAvailable = true,
            Conv = conv,
            TargetNatType = targetNode.NatType
        });
    }

    private Task HandleHolePunchReadyAsync(string clientId, HolePunchStart message)
    {
        ConsoleStatusWriter.WriteServerStatus("Signaling", $"Hole punch ready for client {clientId}, session {message.SessionId}", ConsoleColor.DarkGray);
        return Task.CompletedTask;
    }

    private async Task HandleRelayRequestAsync(string clientId, RelayRequest message)
    {
        _telemetry.Increment("relay_requests");
        if (!_nodes.TryGetValue(message.TargetNodeId, out var targetNode))
        {
            await SendToClientAsync(clientId, new ErrorMessage
            {
                Code = 404,
                Message = $"Target node {message.TargetNodeId} not found"
            });
            return;
        }

        var sessionId = string.IsNullOrWhiteSpace(message.SessionId)
            ? Guid.NewGuid().ToString("N")[..8]
            : message.SessionId;

        await SendToClientAsync(clientId, new RelayAccept
        {
            RelayPort = _config.RelayPort,
            SessionId = sessionId,
            Role = "extranet"
        });

        await SendToClientAsync(targetNode.ClientId, new RelayAccept
        {
            RelayPort = _config.RelayPort,
            SessionId = sessionId,
            Role = "intranet"
        });
    }

    public async Task SendToClientAsync(string clientId, BaseMessage message)
    {
        if (!_connections.TryGetValue(clientId, out var conn))
        {
            return;
        }

        try
        {
            await conn.SendAsync(message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            ConsoleStatusWriter.WriteServerStatus("Signaling", $"Send error to {clientId}: {ex.Message}", ConsoleColor.Red);
        }
    }

    private bool ValidateRegistrationToken(string token)
    {
        if (!_config.RequireRegistrationToken)
        {
            return true;
        }

        return _config.RegistrationTokens.Contains(token, StringComparer.Ordinal);
    }

    /// <summary>
    /// Transport-agnostic disconnect. Removes the connection from the registry and
    /// closes it so its receive loop exits and runs the finally cleanup (which fires
    /// the disconnect telemetry / node-binding removal). Does NOT do node cleanup
    /// here — the receive-loop finally (TCP) or <see cref="UnregisterConnection"/>
    /// (WS) does that, mirroring the pre-refactor single-fire semantics.
    /// </summary>
    private async Task DisconnectClientAsync(string clientId)
    {
        if (_connections.TryRemove(clientId, out var conn))
        {
            try
            {
                await conn.DisconnectAsync();
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _listener.Stop();

        foreach (var conn in _connections.Values)
        {
            if (conn is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }

        _connections.Clear();
        _nodes.Clear();
        _clientToNode.Clear();
    }
}
