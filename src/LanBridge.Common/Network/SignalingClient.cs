using System.Net.Sockets;
using System.Text;

namespace LanBridge.Common.Network;

/// <summary>
/// TCP-based signaling transport with 4-byte length-prefixed JSON messages.
/// Now extends SignalingTransportBase for transport abstraction.
/// </summary>
public class SignalingClient : SignalingTransportBase
{
    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private readonly string _serverHost;
    private readonly int _serverPort;

    public override bool IsConnected => _tcpClient?.Connected == true && _stream != null;

    public SignalingClient(string host, int port)
    {
        _serverHost = host;
        _serverPort = port;
    }

    public async Task ConnectAsync()
    {
        try
        {
            _tcpClient = new TcpClient();
            await _tcpClient.ConnectAsync(_serverHost, _serverPort);
            _stream = _tcpClient.GetStream();
            SetConnected(true);
            _ = Task.Run(ReceiveLoopAsync);
        }
        catch (Exception ex)
        {
            HandleError($"Connect error: {ex.Message}");
            throw;
        }
    }

    protected override async Task SendCoreAsync(string message, CancellationToken ct)
    {
        if (_stream == null)
        {
            throw new InvalidOperationException("Not connected");
        }

        var data = Encoding.UTF8.GetBytes(message);
        var lengthBytes = BitConverter.GetBytes(data.Length);
        await _stream.WriteAsync(lengthBytes, 0, 4, ct);
        await _stream.WriteAsync(data, 0, data.Length, ct);
        await _stream.FlushAsync(ct);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[65536];

        try
        {
            while (IsConnected && _stream != null)
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
                await HandleMessageAsync(message);
            }
        }
        catch (Exception ex)
        {
            if (IsConnected)
            {
                HandleError($"Receive error: {ex.Message}");
                HandleDisconnected();
            }
        }
    }

    protected override void DisposeCore()
    {
        _stream?.Dispose();
        _tcpClient?.Dispose();
    }
}
