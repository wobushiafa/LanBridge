using System.Net.Sockets;
using System.Text;

namespace LanBridge.Common.Network;

public class SignalingClient : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly string _serverHost;
    private readonly int _serverPort;
    private CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private bool _isConnected;

    public event Action<string>? OnMessageReceived;
    public event Action? OnDisconnected;
    public event Action<string>? OnError;

    public bool IsConnected => _isConnected;

    public SignalingClient(string host, int port)
    {
        _serverHost = host;
        _serverPort = port;
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync()
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_serverHost, _serverPort);
            _stream = _tcpClient.GetStream();
            _isConnected = true;
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            OnError?.Invoke($"Connect error: {ex.Message}");
            throw;
        }
    }

    public async Task SendAsync(string message)
    {
        if (!_isConnected || _stream == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            var data = Encoding.UTF8.GetBytes(message);
            var lengthBytes = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(lengthBytes, 0, 4);
            await _stream.WriteAsync(data, 0, data.Length);
            await _stream.FlushAsync();
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];

        try
        {
            while (_isConnected && _stream != null)
            {
                var lengthBuffer = new byte[4];
                var bytesRead = 0;
                while (bytesRead < 4)
                {
                    var read = await _stream.ReadAsync(lengthBuffer, bytesRead, 4 - bytesRead);
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
                    var read = await _stream.ReadAsync(buffer, bytesRead, messageLength - bytesRead);
                    if (read == 0)
                    {
                        throw new IOException("Connection closed");
                    }

                    bytesRead += read;
                }

                var message = Encoding.UTF8.GetString(buffer, 0, messageLength);
                OnMessageReceived?.Invoke(message);
            }
        }
        catch (Exception ex)
        {
            if (_isConnected)
            {
                OnError?.Invoke($"Receive error: {ex.Message}");
                _isConnected = false;
                OnDisconnected?.Invoke();
            }
        }
    }

    public void Dispose()
    {
        _isConnected = false;
        _cts.Cancel();
        _cts.Dispose();
        _sendLock.Dispose();
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}
