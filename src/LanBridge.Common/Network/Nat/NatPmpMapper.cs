using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace LanBridge.Common.Network.Nat;

public sealed class NatPmpMapper : INatMapper
{
    private readonly IPAddress _gatewayAddress;
    private readonly UdpClient _udpClient;
    private bool _isDisposed;

    public string Protocol => "NAT-PMP";

    public NatPmpMapper(IPAddress gatewayAddress)
    {
        _gatewayAddress = gatewayAddress ?? throw new ArgumentNullException(nameof(gatewayAddress));
        _udpClient = new UdpClient();
        // 设置较短的 Socket 选项，以防资源泄露
        _udpClient.Client.SendTimeout = 1000;
        _udpClient.Client.ReceiveTimeout = 1000;
    }

    public async Task<IPAddress?> GetExternalIpAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = new byte[2];
        request[0] = 0; // Version
        request[1] = 0; // OP (External Address Request)

        byte[]? response = await SendAndReceiveWithRetryAsync(request, cancellationToken);
        if (response == null || response.Length < 12)
        {
            return null;
        }

        // 验证响应头部
        if (response[0] != 0 || response[1] != 128)
        {
            return null; // 不是预期的 Version/OP
        }

        ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        if (resultCode != 0)
        {
            return null; // 错误响应
        }

        byte[] ipBytes = new byte[4];
        Array.Copy(response, 8, ipBytes, 0, 4);
        return new IPAddress(ipBytes);
    }

    public async Task<int> CreatePortMappingAsync(int localPort, int externalPort, int lifetimeSeconds, CancellationToken cancellationToken = default)
    {
        if (localPort <= 0 || localPort > 65535) throw new ArgumentOutOfRangeException(nameof(localPort));
        if (externalPort < 0 || externalPort > 65535) throw new ArgumentOutOfRangeException(nameof(externalPort));

        byte[] request = new byte[12];
        request[0] = 0; // Version
        request[1] = 1; // OP (Map UDP)
        // 2-3 Reserved = 0
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4, 2), (ushort)localPort);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6, 2), (ushort)externalPort);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8, 4), (uint)lifetimeSeconds);

        byte[]? response = await SendAndReceiveWithRetryAsync(request, cancellationToken);
        if (response == null || response.Length < 16)
        {
            return 0;
        }

        if (response[0] != 0 || response[1] != 129) // OP Map UDP Response is 129
        {
            return 0;
        }

        ushort resultCode = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2, 2));
        if (resultCode != 0)
        {
            return 0; // 失败
        }

        ushort mappedExternalPort = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10, 2));
        return mappedExternalPort;
    }

    public async Task DeletePortMappingAsync(int localPort, int externalPort, CancellationToken cancellationToken = default)
    {
        // 按照 NAT-PMP 规范，删除映射就是创建一个生命期为 0 的映射
        await CreatePortMappingAsync(localPort, externalPort, 0, cancellationToken);
    }

    private async Task<byte[]?> SendAndReceiveWithRetryAsync(byte[] request, CancellationToken cancellationToken)
    {
        var targetEp = new IPEndPoint(_gatewayAddress, 5351);
        int retryDelayMs = 250;
        int maxRetries = 4; // 稍微控制重试次数以防阻塞太久

        for (int i = 0; i < maxRetries; i++)
        {
            if (_isDisposed) return null;

            try
            {
                await _udpClient.SendAsync(request, request.Length, targetEp);

                // 使用带有 CancellationToken 限制的超时机制进行接收
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(retryDelayMs);

                var receiveResult = await _udpClient.ReceiveAsync(cts.Token);
                
                // 校验数据来源是否是网关
                if (receiveResult.RemoteEndPoint.Address.Equals(_gatewayAddress))
                {
                    return receiveResult.Buffer;
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // 超时，等待下一次重试，退避时间加倍
                retryDelayMs *= 2;
            }
            catch
            {
                // 其他异常（如套接字关闭等），直接返回失败
                return null;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (!_isDisposed)
        {
            _isDisposed = true;
            _udpClient.Dispose();
        }
    }
}
