using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using LanBridge.Common.Network.Nat;
using Xunit;

namespace LanBridge.Tests;

public class NatMappingTests
{
    [Fact]
    public void DefaultGateway_Check_NoException()
    {
        // 应该能正常执行不报错，即使在无网关环境下也只返回 null，不抛出异常
        var gw = NatMappingManager.GetDefaultGateway();
    }

    [Fact]
    public async Task NatPmpMapper_Loopback_CreatePortMapping_Success()
    {
        int localPortToMap = 12345;
        int requestedExternalPort = 23456;
        int lifeTime = 3600;

        // 开启本地 NAT-PMP 模拟服务器，端口为 5351
        UdpClient? mockGateway = null;
        try
        {
            mockGateway = new UdpClient(new IPEndPoint(IPAddress.Loopback, 5351));
        }
        catch (SocketException)
        {
            // 端口被占用，或者无权限，跳过此测试
            return;
        }

        using (mockGateway)
        {
            using var cts = new CancellationTokenSource(3000);

            // 启动后台模拟网关应答任务
            var gatewayTask = Task.Run(async () =>
            {
                try
                {
                    var receiveResult = await mockGateway.ReceiveAsync(cts.Token);
                    var req = receiveResult.Buffer;

                    // 校验请求报文
                    if (req.Length >= 12 && req[0] == 0 && req[1] == 1) // UDP map
                    {
                        ushort localPort = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(4, 2));
                        ushort extPort = BinaryPrimitives.ReadUInt16BigEndian(req.AsSpan(6, 2));

                        // 构造响应 (OP=129)
                        byte[] resp = new byte[16];
                        resp[0] = 0;   // Version
                        resp[1] = 129; // OP (Map UDP Response)
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(2, 2), 0); // Result Code 0 (Success)
                        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(4, 4), 1000); // Epoch
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(8, 2), localPort);
                        BinaryPrimitives.WriteUInt16BigEndian(resp.AsSpan(10, 2), (ushort)(extPort == 0 ? localPort : extPort));
                        BinaryPrimitives.WriteUInt32BigEndian(resp.AsSpan(12, 4), 3600);

                        await mockGateway.SendAsync(resp, resp.Length, receiveResult.RemoteEndPoint);
                    }
                }
                catch
                {
                    // 忽略异常
                }
            });

            // 实例化并测试 NatPmpMapper，指定网关为 Loopback
            using var mapper = new NatPmpMapper(IPAddress.Loopback);
            int mappedExternalPort = await mapper.CreatePortMappingAsync(localPortToMap, requestedExternalPort, lifeTime, cts.Token);

            await gatewayTask;

            Assert.Equal(requestedExternalPort, mappedExternalPort);
        }
    }
}
