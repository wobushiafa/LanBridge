using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LanBridge.Common.Network;

public class SignalingServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Dictionary<string, TcpClient> _clients = new();
    private readonly Dictionary<string, NetworkStream> _streams = new();
    private CancellationTokenSource _cts;
    private bool _isRunning;

    public event Action<string, string>? OnClientMessage;
    public event Action<string>? OnClientConnected;
    public event Action<string>? OnClientDisconnected;

    public SignalingServer(int port)
    {
        try
        {
            _listener = new TcpListener(IPAddress.IPv6Any, port);
            _listener.Server.DualMode = true;
        }
        catch
        {
            _listener = new TcpListener(IPAddress.Any, port);
        }

        _cts = new CancellationTokenSource();
    }

    public async Task StartAsync()
    {
        _listener.Start();
        _isRunning = true;

        while (_isRunning)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync();
                var clientId = Guid.NewGuid().ToString("N")[..8];

                _clients[clientId] = client;
                _streams[clientId] = client.GetStream();
                OnClientConnected?.Invoke(clientId);
                _ = Task.Run(() => HandleClientAsync(clientId, client));
            }
            catch (Exception) when (!_isRunning)
            {
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task HandleClientAsync(string clientId, TcpClient client)
    {
        var buffer = new byte[65536];

        try
        {
            var stream = client.GetStream();
            while (client.Connected)
            {
                var lengthBuffer = new byte[4];
                var bytesRead = 0;
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
                OnClientMessage?.Invoke(clientId, message);
            }
        }
        catch (Exception)
        {
        }
        finally
        {
            _clients.Remove(clientId);
            _streams.Remove(clientId);
            client.Dispose();
            OnClientDisconnected?.Invoke(clientId);
        }
    }

    public async Task SendToClientAsync(string clientId, string message)
    {
        if (!_streams.TryGetValue(clientId, out var stream))
        {
            throw new ArgumentException($"Client {clientId} not found");
        }

        var data = Encoding.UTF8.GetBytes(message);
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await stream.WriteAsync(lengthBytes, 0, 4);
        await stream.WriteAsync(data, 0, data.Length);
        await stream.FlushAsync();
    }

    public void RemoveClient(string clientId)
    {
        if (_clients.TryGetValue(clientId, out var client))
        {
            client.Dispose();
            _clients.Remove(clientId);
            _streams.Remove(clientId);
        }
    }

    public void Dispose()
    {
        _isRunning = false;
        _cts.Cancel();
        _cts.Dispose();
        _listener.Stop();

        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
        _streams.Clear();
    }
}
