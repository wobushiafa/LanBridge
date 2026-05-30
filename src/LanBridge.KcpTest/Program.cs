using System;
using System.Collections.Generic;
using LanBridge.Common.Kcp;

namespace LanBridge.KcpTest;

class Program
{
    static void Main(string[] args)
    {
        VerifyImplementation();

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("======================================================================");
        Console.WriteLine("        LanBridge KCP Weak Network Performance Simulation Audit       ");
        Console.WriteLine("======================================================================");
        Console.ResetColor();

        // 弱网环境配置
        double lossRate = 0.15; // 15% 随机丢包
        int baseLatencyMs = 60; // 60ms 单向延迟 (RTT ~120ms)
        int jitterMs = 20;      // ±20ms 延迟抖动
        int transferSize = 2 * 1024 * 1024; // 2 MB 数据传输

        Console.WriteLine($"[Config] Transfer Size : {transferSize / (1024.0 * 1024.0):F2} MB");
        Console.WriteLine($"[Config] Packet Loss   : {lossRate * 100:F1}%");
        Console.WriteLine($"[Config] Base Latency  : {baseLatencyMs} ms (RTT ~{baseLatencyMs * 2} ms)");
        Console.WriteLine($"[Config] Latency Jitter: ±{jitterMs} ms");
        Console.WriteLine();

        // 运行 Legacy 模式模拟
        var resultLegacy = RunSimulation(true, false, lossRate, baseLatencyMs, jitterMs, transferSize);

        // 运行 Optimized 模式模拟 (标准拥塞控制 + 新 RTT/RTO 追踪 + 快速恢复 + 快速重传)
        var resultOptimized = RunSimulation(false, false, lossRate, baseLatencyMs, jitterMs, transferSize);

        // 运行 Optimized + Adaptive Congestion 模式模拟 (自适应抗无线随机丢包)
        var resultAdaptive = RunSimulation(false, true, lossRate, baseLatencyMs, jitterMs, transferSize);

        // 输出可视化比对表
        PrintComparisonReport(resultLegacy, resultOptimized, resultAdaptive, transferSize);
    }

    private sealed class SimulationResult
    {
        public string ModeName { get; init; } = "";
        public bool Success { get; init; }
        public uint TotalSimulatedTimeMs { get; init; }
        public uint TotalSentPackets { get; init; }
        public uint TotalBytesReceived { get; init; }
        public uint FinalSrtt { get; init; }
        public uint FinalRto { get; init; }
        public uint MaxCwnd { get; init; }
        public uint FinalCwnd { get; init; }
    }

    private static SimulationResult RunSimulation(
        bool legacyMode, 
        bool adaptiveCongestion, 
        double lossRate, 
        int baseLatencyMs, 
        int jitterMs, 
        int transferSize)
    {
        string modeName = legacyMode ? "Legacy KCP" : (adaptiveCongestion ? "Optimized (Adaptive CC)" : "Optimized (Standard CC)");
        Console.WriteLine($"---> Starting {modeName} Simulation...");

        // 用于统计发送总包数的计数器
        uint sentPackets = 0;

        // 初始化 Sender 和 Receiver KCP 实例
        // 模拟 UDP 单向输出，将报文塞入 SimulatedChannel 模拟丢包、延迟与抖动
        Kcp sender = null!;
        Kcp receiver = null!;
        uint currentTime = 0; // 当前模拟时间

        SimulatedChannel senderToReceiver = new SimulatedChannel((data, len) =>
        {
            receiver.Input(data, 0, len);
        }, lossRate, baseLatencyMs, jitterMs);

        SimulatedChannel receiverToSender = new SimulatedChannel((data, len) =>
        {
            sender.Input(data, 0, len);
        }, lossRate, baseLatencyMs, jitterMs);

        sender = new Kcp(1, (data, len, ep) =>
        {
            sentPackets++;
            senderToReceiver.Send(data, len, currentTime); // 捕获 currentTime
        });

        receiver = new Kcp(1, (data, len, ep) =>
        {
            receiverToSender.Send(data, len, currentTime); // 捕获 currentTime
        });

        // 均启用 nodelay 模式: 1=nodelay, 10=10ms时钟, 2=快速重传(2次dup ack触发), 0=启用拥塞控制
        sender.SetNodelay(1, 10, 2, 0);
        receiver.SetNodelay(1, 10, 2, 0);
        
        sender.WndSize(1024, 1024);
        receiver.WndSize(1024, 1024);

        sender.EnableLegacyMode = legacyMode;
        receiver.EnableLegacyMode = legacyMode;
        sender.UseAdaptiveCongestion = adaptiveCongestion;
        receiver.UseAdaptiveCongestion = adaptiveCongestion;

        // 准备发送数据 payload
        byte[] payload = new byte[1000];
        Array.Fill(payload, (byte)0x41); // 'A'

        int totalSent = 0;
        int totalReceived = 0;
        byte[] recvBuffer = new byte[4096];

        uint nextSenderUpdate = 0;
        uint nextReceiverUpdate = 0;
        uint maxCwnd = 1;

        bool success = false;

        // 离散时间驱动循环 (每次循环模拟 1ms)
        while (currentTime < 1000000) // 限制最大模拟时长 1000 秒，防止死锁
        {
            // 1. 发送端填充队列
            while (totalSent < transferSize && sender.WaitSnd() < 512)
            {
                int size = Math.Min(1000, transferSize - totalSent);
                sender.Send(payload, 0, size);
                totalSent += size;
            }

            // 2. KCP 内部时钟心跳 (nodelay 最小间隔 10ms)
            if (currentTime >= nextSenderUpdate)
            {
                sender.Update(currentTime);
                nextSenderUpdate = currentTime + 10;
            }
            if (currentTime >= nextReceiverUpdate)
            {
                receiver.Update(currentTime);
                nextReceiverUpdate = currentTime + 10;
            }

            // 3. 驱动模拟信道（处理报文延迟到达与投递）
            senderToReceiver.Update(currentTime);
            receiverToSender.Update(currentTime);

            // 4. 接收端拉取数据
            while (true)
            {
                int len = receiver.Receive(recvBuffer, 0, recvBuffer.Length);
                if (len < 0) break;
                totalReceived += len;
            }

            if (sender.Cwnd > maxCwnd)
            {
                maxCwnd = sender.Cwnd;
            }

            // 5. 检查是否传输完毕
            if (totalReceived >= transferSize)
            {
                success = true;
                break;
            }

            currentTime++;
        }

        Console.WriteLine($"    Finished {modeName} in Simulated Time: {currentTime} ms, Sent Packets: {sentPackets}");
        return new SimulationResult
        {
            ModeName = modeName,
            Success = success,
            TotalSimulatedTimeMs = currentTime,
            TotalSentPackets = sentPackets,
            TotalBytesReceived = (uint)totalReceived,
            FinalSrtt = sender.Srtt,
            FinalRto = sender.Rto,
            MaxCwnd = maxCwnd,
            FinalCwnd = sender.Cwnd
        };
    }

    private static void PrintComparisonReport(
        SimulationResult legacy, 
        SimulationResult optimized, 
        SimulationResult adaptive, 
        int transferSize)
    {
        double legacySpeed = (transferSize / 1024.0 / 1024.0) / (legacy.TotalSimulatedTimeMs / 1000.0);
        double optSpeed = (transferSize / 1024.0 / 1024.0) / (optimized.TotalSimulatedTimeMs / 1000.0);
        double adaptSpeed = (transferSize / 1024.0 / 1024.0) / (adaptive.TotalSimulatedTimeMs / 1000.0);

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("======================================================================");
        Console.WriteLine("                       KCP SIMULATION PERFORMANCE AUDIT               ");
        Console.WriteLine("======================================================================");
        Console.ResetColor();

        Console.WriteLine($"{"Metric",-30} | {"Legacy KCP",-12} | {"Optimized (Std CC)",-18} | {"Optimized (Adapt CC)",-20}");
        Console.WriteLine(new string('-', 90));

        Console.WriteLine($"{"Status",-30} | {(legacy.Success ? "SUCCESS" : "TIMEOUT"),-12} | {(optimized.Success ? "SUCCESS" : "TIMEOUT"),-18} | {(adaptive.Success ? "SUCCESS" : "TIMEOUT"),-20}");
        Console.WriteLine($"{"Simulated Time (ms)",-30} | {legacy.TotalSimulatedTimeMs,-12} | {optimized.TotalSimulatedTimeMs,-18} | {adaptive.TotalSimulatedTimeMs,-20}");
        
        Console.WriteLine($"{"Throughput (MB/s)",-30} | {legacySpeed,-12:F3} | {optSpeed,-18:F3} | {adaptSpeed,-20:F3}");
        
        Console.WriteLine($"{"Total Sent Packets (Overhead)",-30} | {legacy.TotalSentPackets,-12} | {optimized.TotalSentPackets,-18} | {adaptive.TotalSentPackets,-20}");
        Console.WriteLine($"{"Final Smoothed RTT (ms)",-30} | {legacy.FinalSrtt,-12} | {optimized.FinalSrtt,-18} | {adaptive.FinalSrtt,-20}");
        Console.WriteLine($"{"Final Retransmit RTO (ms)",-30} | {legacy.FinalRto,-12} | {optimized.FinalRto,-18} | {adaptive.FinalRto,-20}");
        Console.WriteLine($"{"Max Congestion Window (Cwnd)",-30} | {legacy.MaxCwnd,-12} | {optimized.MaxCwnd,-18} | {adaptive.MaxCwnd,-20}");
        Console.WriteLine($"{"Final Congestion Window (Cwnd)",-30} | {legacy.FinalCwnd,-12} | {optimized.FinalCwnd,-18} | {adaptive.FinalCwnd,-20}");
        Console.WriteLine(new string('-', 90));

        Console.ForegroundColor = ConsoleColor.Yellow;
        double optSpeedup = (double)legacy.TotalSimulatedTimeMs / optimized.TotalSimulatedTimeMs;
        double adaptSpeedup = (double)legacy.TotalSimulatedTimeMs / adaptive.TotalSimulatedTimeMs;
        Console.WriteLine($"[Conclusion] Optimized (Std CC)  Speedup: {optSpeedup:F2}x faster transfer speed!");
        Console.WriteLine($"[Conclusion] Optimized (Adapt CC) Speedup: {adaptSpeedup:F2}x faster transfer speed!");
        Console.ResetColor();
        Console.WriteLine("======================================================================");
        Console.WriteLine();
    }

    private static void VerifyImplementation()
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[Audit] Running Implementation Verifications...");
        Console.ResetColor();

        // 1. Verify UnreliableData frame type encoding/decoding
        uint streamId = 12345;
        byte[] originalPayload = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };
        var unreliableFrame = LanBridge.Common.Protocol.TunnelFrame.UnreliableData(streamId, originalPayload, 0, originalPayload.Length);
        
        if (unreliableFrame.Type != LanBridge.Common.Protocol.TunnelFrameType.UnreliableData)
        {
            throw new Exception("Frame type is not UnreliableData!");
        }

        byte[] encoded = unreliableFrame.Encode();
        if (!LanBridge.Common.Protocol.TunnelFrame.TryDecode(encoded, encoded.Length, out var decodedFrame))
        {
            throw new Exception("Failed to decode UnreliableData frame!");
        }

        if (decodedFrame.Type != LanBridge.Common.Protocol.TunnelFrameType.UnreliableData)
        {
            throw new Exception($"Decoded frame type mismatch: expected UnreliableData, got {decodedFrame.Type}");
        }

        if (decodedFrame.StreamId != streamId)
        {
            throw new Exception($"Decoded stream ID mismatch: expected {streamId}, got {decodedFrame.StreamId}");
        }

        if (!decodedFrame.Payload.Span.SequenceEqual(originalPayload))
        {
            throw new Exception("Decoded payload content mismatch!");
        }

        Console.WriteLine("  [Pass] UnreliableData frame encoding/decoding verified.");

        // 2. Verify MessageJsonContext serialization of StunNatType
        var regMsg = new LanBridge.Common.Protocol.RegisterMessage
        {
            NodeId = "test-node",
            Token = "token",
            NatType = LanBridge.Common.Protocol.StunNatType.Symmetric
        };
        var serialized = LanBridge.Common.Protocol.MessageSerializer.SerializeToString(regMsg);
        if (!serialized.Contains("\"nat_type\":5"))
        {
            throw new Exception($"JSON serialization failed: expected nat_type 5 (Symmetric) in: {serialized}");
        }

        var deserialized = LanBridge.Common.Protocol.MessageSerializer.Deserialize<LanBridge.Common.Protocol.RegisterMessage>(serialized);
        if (deserialized == null || deserialized.NatType != LanBridge.Common.Protocol.StunNatType.Symmetric)
        {
            throw new Exception("JSON deserialization of StunNatType failed!");
        }

        Console.WriteLine("  [Pass] MessageJsonContext AOT serialization verified.");
        Console.WriteLine();
    }
}

/// <summary>
/// 模拟的高性能弱网丢包抖动单向 UDP 信道
/// </summary>
public class SimulatedChannel
{
    private readonly Action<byte[], int> _deliverAction;
    private readonly double _lossRate;
    private readonly int _baseLatencyMs;
    private readonly int _jitterMs;
    
    private readonly List<(byte[] data, uint deliveryTime)> _queue = new();
    private readonly object _lock = new();

    public SimulatedChannel(Action<byte[], int> deliverAction, double lossRate, int baseLatencyMs, int jitterMs)
    {
        _deliverAction = deliverAction;
        _lossRate = lossRate;
        _baseLatencyMs = baseLatencyMs;
        _jitterMs = jitterMs;
    }

    public void Send(byte[] data, int length, uint currentTime)
    {
        // 模拟随机丢包
        if (Random.Shared.NextDouble() < _lossRate)
        {
            return;
        }

        // 模拟延迟与抖动
        int delay = _baseLatencyMs;
        if (_jitterMs > 0)
        {
            delay += Random.Shared.Next(-_jitterMs, _jitterMs + 1);
            if (delay < 0) delay = 0;
        }

        byte[] copy = new byte[length];
        Array.Copy(data, 0, copy, 0, length);

        lock (_lock)
        {
            _queue.Add((copy, currentTime + (uint)delay));
        }
    }

    public void Update(uint currentTime)
    {
        List<byte[]> toDeliver = new();
        lock (_lock)
        {
            for (int i = _queue.Count - 1; i >= 0; i--)
            {
                if (currentTime >= _queue[i].deliveryTime)
                {
                    toDeliver.Add(_queue[i].data);
                    _queue.RemoveAt(i);
                }
            }
        }

        // 在锁外部投递，防死锁
        foreach (var data in toDeliver)
        {
            _deliverAction(data, data.Length);
        }
    }
}
