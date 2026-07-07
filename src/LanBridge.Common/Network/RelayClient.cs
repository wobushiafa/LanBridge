using System.Net.Sockets;

namespace LanBridge.Common.Network;

public class RelayClient : IDisposable
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly CancellationTokenSource _cts;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _isConnected;

    public event Action<byte[], int>? OnDataReceived;
    public event Action? OnDisconnected;

    public bool IsConnected => _isConnected;

    public RelayClient()
    {
        _cts = new CancellationTokenSource();
    }

    public async Task ConnectAsync(string host, int port)
    {
        _tcpClient = new TcpClient();
        await _tcpClient.ConnectAsync(host, port);
        _stream = _tcpClient.GetStream();
        _isConnected = true;
        _ = Task.Run(ReceiveLoopAsync);
    }

    public async Task SendAsync(byte[] data, int offset, int length)
    {
        if (!_isConnected || _stream == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        await _sendLock.WaitAsync(_cts.Token);
        try
        {
            var lengthBytes = BitConverter.GetBytes(length);
            await _stream.WriteAsync(lengthBytes, 0, 4);
            await _stream.WriteAsync(data, offset, length);
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

                var payload = buffer.AsSpan(0, messageLength).ToArray();
                OnDataReceived?.Invoke(payload, payload.Length);
            }
        }
        catch (Exception)
        {
            if (_isConnected)
            {
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
