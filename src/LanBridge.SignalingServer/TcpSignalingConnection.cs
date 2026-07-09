using System.Net.Sockets;
using System.Text;
using LanBridge.Common.Protocol;

namespace LanBridge.SignalingServer;

/// <summary>
/// TCP-backed <see cref="ISignalingConnection"/>. Wraps a <see cref="TcpClient"/>,
/// its <see cref="NetworkStream"/>, and a per-connection send lock. Writes the
/// 4-byte little-endian length prefix + UTF-8 JSON body framing used by the TCP
/// signaling protocol. <see cref="Stream"/> and <see cref="Connected"/> are exposed
/// for the server's TCP receive loop.
/// </summary>
public sealed class TcpSignalingConnection : ISignalingConnection, IDisposable
{
    private readonly TcpClient _tcpClient;
    private readonly NetworkStream _stream;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public TcpSignalingConnection(TcpClient client)
    {
        _tcpClient = client;
        _stream = client.GetStream();
    }

    /// <summary>
    /// Underlying network stream for the server receive loop
    /// (read 4-byte length prefix + JSON body).
    /// </summary>
    public NetworkStream Stream => _stream;

    /// <summary>
    /// Whether the underlying socket is still connected. Used by the receive
    /// loop's <c>while</c> guard, matching the pre-refactor <c>TcpClient.Connected</c> check.
    /// </summary>
    public bool Connected => _tcpClient.Connected;

    public async Task SendAsync(BaseMessage message, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct);
        try
        {
            var data = Encoding.UTF8.GetBytes(MessageSerializer.SerializeToString(message));
            var lengthBytes = BitConverter.GetBytes(data.Length);
            await _stream.WriteAsync(lengthBytes, 0, 4, ct);
            await _stream.WriteAsync(data, 0, data.Length, ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>
    /// Acquire the send lock so a concurrent <see cref="SendAsync"/> is not mid-write
    /// when the stream is disposed (it will fail on the disposed stream and be caught
    /// upstream). Then dispose the stream + socket + lock. Idempotent via the
    /// underlying <see cref="NetworkStream"/>/<see cref="TcpClient"/>/
    /// <see cref="SemaphoreSlim"/> disposables.
    /// </summary>
    public async Task DisconnectAsync()
    {
        await _sendLock.WaitAsync();
        try
        {
            _stream.Dispose();
            _tcpClient.Dispose();
        }
        finally
        {
            _sendLock.Release();
            _sendLock.Dispose();
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _tcpClient.Dispose();
        _sendLock.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
