using System.Net;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LanBridge.Common.Network;

namespace LanBridge.Common.Protocol;

/// <summary>
/// 消息类型枚举
/// </summary>
public enum MessageType : byte
{
    // 信令相关
    Register = 10,          // 内网节点注册
    RegisterAck = 11,       // 注册确认
    ConnectRequest = 12,    // 外网节点请求连接
    ConnectReady = 13,      // 内网节点就绪
    HolePunchStart = 14,    // 开始打洞
    HolePunchReady = 15,    // 打洞就绪
    
    // P2P 连接
    P2pConnect = 20,        // P2P 连接请求
    P2pAccept = 21,         // P2P 连接接受
    P2pHeartbeat = 22,      // P2P 心跳
    
    // 中转相关
    RelayRequest = 30,      // 中转请求
    RelayAccept = 31,       // 中转接受
    RelayData = 32,         // 中转数据
    
    // 错误
    Error = 255
}

/// <summary>
/// 基础消息
/// </summary>
public class BaseMessage
{
    [JsonPropertyName("type")]
    public MessageType Type { get; set; }
    
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    
    [JsonPropertyName("conv")]
    public uint Conv { get; set; }
}



/// <summary>
/// 内网节点注册
/// </summary>
public class RegisterMessage : BaseMessage
{
    public RegisterMessage() => Type = MessageType.Register;
    
    [JsonPropertyName("node_id")]
    public string NodeId { get; set; } = string.Empty;
    
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("public_ep")]
    public string? PublicEndPoint { get; set; }

    [JsonPropertyName("public_ep_v6")]
    public string? PublicEndPointV6 { get; set; }

    [JsonPropertyName("nat_type")]
    public StunNatType NatType { get; set; }
}

/// <summary>
/// 注册确认
/// </summary>
public class RegisterAck : BaseMessage
{
    public RegisterAck() => Type = MessageType.RegisterAck;
    
    [JsonPropertyName("success")]
    public bool Success { get; set; }
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 外网节点连接请求
/// </summary>
public class ConnectRequest : BaseMessage
{
    public ConnectRequest() => Type = MessageType.ConnectRequest;
    
    [JsonPropertyName("target_node_id")]
    public string TargetNodeId { get; set; } = string.Empty;
    
    [JsonPropertyName("client_ep")]
    public string? ClientEndPoint { get; set; }

    [JsonPropertyName("client_ep_v6")]
    public string? ClientEndPointV6 { get; set; }

    [JsonPropertyName("nat_type")]
    public StunNatType NatType { get; set; }
}

/// <summary>
/// 连接就绪（包含双方坐标）
/// </summary>
public class ConnectReady : BaseMessage
{
    public ConnectReady() => Type = MessageType.ConnectReady;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;
    
    [JsonPropertyName("intranet_ep")]
    public string IntranetEndPoint { get; set; } = string.Empty;

    [JsonPropertyName("intranet_ep_v6")]
    public string? IntranetEndPointV6 { get; set; }
    
    [JsonPropertyName("extranet_ep")]
    public string ExtranetEndPoint { get; set; } = string.Empty;

    [JsonPropertyName("extranet_ep_v6")]
    public string? ExtranetEndPointV6 { get; set; }
    
    [JsonPropertyName("relay_available")]
    public bool RelayAvailable { get; set; }

    [JsonPropertyName("target_nat_type")]
    public StunNatType TargetNatType { get; set; }
}

/// <summary>
/// 开始打洞
/// </summary>
public class HolePunchStart : BaseMessage
{
    public HolePunchStart() => Type = MessageType.HolePunchStart;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;
    
    [JsonPropertyName("target_ep")]
    public string TargetEndPoint { get; set; } = string.Empty;

    [JsonPropertyName("target_ep_v6")]
    public string? TargetEndPointV6 { get; set; }
    
    [JsonPropertyName("is_initiator")]
    public bool IsInitiator { get; set; }

    [JsonPropertyName("target_nat_type")]
    public StunNatType TargetNatType { get; set; }
}

/// <summary>
/// P2P 连接请求
/// </summary>
public class P2pConnectMessage : BaseMessage
{
    public P2pConnectMessage() => Type = MessageType.P2pConnect;
    
    [JsonPropertyName("conv")]
    public new uint Conv { get; set; }
}

/// <summary>
/// P2P 连接接受
/// </summary>
public class P2pAcceptMessage : BaseMessage
{
    public P2pAcceptMessage() => Type = MessageType.P2pAccept;
    
    [JsonPropertyName("conv")]
    public new uint Conv { get; set; }
}

/// <summary>
/// 中转请求
/// </summary>
public class RelayRequest : BaseMessage
{
    public RelayRequest() => Type = MessageType.RelayRequest;
    
    [JsonPropertyName("target_node_id")]
    public string TargetNodeId { get; set; } = string.Empty;

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;
}

/// <summary>
/// 中转接受
/// </summary>
public class RelayAccept : BaseMessage
{
    public RelayAccept() => Type = MessageType.RelayAccept;
    
    [JsonPropertyName("relay_port")]
    public int RelayPort { get; set; }
    
    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;
}

/// <summary>
/// 中转数据
/// </summary>
public class RelayData : BaseMessage
{
    public RelayData() => Type = MessageType.RelayData;
    
    [JsonPropertyName("data")]
    public string Data { get; set; } = string.Empty;
}

public static class ProxyControl
{
    public const string CloseTarget = "P2P_CONTROL:CLOSE_TARGET";

    public static readonly byte[] CloseTargetBytes = System.Text.Encoding.UTF8.GetBytes(CloseTarget);

    public static bool IsCloseTarget(byte[] data, int length)
    {
        if (length != CloseTargetBytes.Length)
        {
            return false;
        }

        return data.AsSpan(0, length).SequenceEqual(CloseTargetBytes);
    }
}

public static class P2PConv
{
    public static uint FromEndpoints(IPEndPoint first, IPEndPoint second)
    {
        var left = first.ToString();
        var right = second.ToString();
        if (string.CompareOrdinal(left, right) > 0)
        {
            (left, right) = (right, left);
        }

        unchecked
        {
            uint hash = 2166136261;
            foreach (var ch in $"{left}|{right}")
            {
                hash ^= ch;
                hash *= 16777619;
            }

            return hash == 0 ? 1 : hash;
        }
    }
}

public enum TunnelFrameType : byte
{
    Data = 1,
    Open = 2,
    Close = 3,
    Ping = 4,
    Pong = 5,
    Error = 6,
    UnreliableData = 7
}

public sealed class TunnelFrame
{
    public const uint Magic = 0x31503250; // P2P1
    private const byte Version = 1;
    public const int HeaderSize = 16;
    private const int MaxPayloadLength = 16 * 1024 * 1024;

    public TunnelFrameType Type { get; init; }
    public uint StreamId { get; init; }
    public ReadOnlyMemory<byte> Payload { get; init; } = ReadOnlyMemory<byte>.Empty;

    public static TunnelFrame Data(uint streamId, ReadOnlyMemory<byte> payload)
    {
        return new TunnelFrame { Type = TunnelFrameType.Data, StreamId = streamId, Payload = payload };
    }

    public static TunnelFrame Data(uint streamId, byte[] payload, int offset, int length)
    {
        return new TunnelFrame { Type = TunnelFrameType.Data, StreamId = streamId, Payload = new ReadOnlyMemory<byte>(payload, offset, length) };
    }

    public static TunnelFrame UnreliableData(uint streamId, byte[] payload, int offset, int length)
    {
        return new TunnelFrame { Type = TunnelFrameType.UnreliableData, StreamId = streamId, Payload = new ReadOnlyMemory<byte>(payload, offset, length) };
    }

    public static TunnelFrame Open(uint streamId, string target)
    {
        return new TunnelFrame
        {
            Type = TunnelFrameType.Open,
            StreamId = streamId,
            Payload = Encoding.UTF8.GetBytes(target)
        };
    }

    public static TunnelFrame Close(uint streamId)
    {
        return new TunnelFrame { Type = TunnelFrameType.Close, StreamId = streamId };
    }

    public static TunnelFrame Ping(byte[] payload)
    {
        return new TunnelFrame { Type = TunnelFrameType.Ping, StreamId = 0, Payload = payload };
    }

    public static TunnelFrame Pong(byte[] payload)
    {
        return new TunnelFrame { Type = TunnelFrameType.Pong, StreamId = 0, Payload = payload };
    }

    public static TunnelFrame Ping(ReadOnlyMemory<byte> payload)
    {
        return new TunnelFrame { Type = TunnelFrameType.Ping, StreamId = 0, Payload = payload };
    }

    public static TunnelFrame Pong(ReadOnlyMemory<byte> payload)
    {
        return new TunnelFrame { Type = TunnelFrameType.Pong, StreamId = 0, Payload = payload };
    }

    public static TunnelFrame Error(uint streamId, string message)
    {
        return new TunnelFrame
        {
            Type = TunnelFrameType.Error,
            StreamId = streamId,
            Payload = Encoding.UTF8.GetBytes(message)
        };
    }

    public string PayloadAsString()
    {
        return Payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Payload.Span);
    }

    public byte[] Encode()
    {
        if (Payload.Length > MaxPayloadLength)
        {
            throw new InvalidDataException($"Tunnel frame payload too large: {Payload.Length}");
        }

        var buffer = new byte[HeaderSize + Payload.Length];
        WriteHeader(buffer, 0, Type, StreamId, Payload.Length);
        Payload.CopyTo(buffer.AsMemory(HeaderSize));
        return buffer;
    }

    public static void WriteHeader(byte[] buffer, int offset, TunnelFrameType type, uint streamId, int payloadLength)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset, 4), Magic);
        buffer[offset + 4] = Version;
        buffer[offset + 5] = (byte)type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(offset + 6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(offset + 8, 4), streamId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(offset + 12, 4), payloadLength);
    }

    public static bool TryDecode(byte[] data, int length, out TunnelFrame frame)
    {
        frame = new TunnelFrame();
        if (length < HeaderSize)
        {
            return false;
        }

        var span = data.AsSpan(0, length);
        if (BinaryPrimitives.ReadUInt32LittleEndian(span[..4]) != Magic || span[4] != Version)
        {
            return false;
        }

        var payloadLength = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(12, 4));
        if (payloadLength < 0 || payloadLength > MaxPayloadLength || HeaderSize + payloadLength != length)
        {
            return false;
        }

        var type = (TunnelFrameType)span[5];
        if (!Enum.IsDefined(type))
        {
            return false;
        }

        // 零拷贝：直接以 Memory 切片形式引用底层 KCP/UDP 数据包，无任何数据复制和堆内存分配！
        var payload = new ReadOnlyMemory<byte>(data, HeaderSize, payloadLength);

        frame = new TunnelFrame
        {
            Type = type,
            StreamId = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(8, 4)),
            Payload = payload
        };
        return true;
    }
}

/// <summary>
/// 错误消息
/// </summary>
public class ErrorMessage : BaseMessage
{
    public ErrorMessage() => Type = MessageType.Error;
    
    [JsonPropertyName("code")]
    public int Code { get; set; }
    
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

/// <summary>
/// 消息序列化/反序列化
/// </summary>
public static class MessageSerializer
{
    /// <summary>
    /// 序列化消息为 JSON 字节
    /// </summary>
    public static byte[] Serialize(BaseMessage message)
    {
        return message switch
        {
            RegisterMessage msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.RegisterMessage),
            RegisterAck msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.RegisterAck),
            ConnectRequest msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.ConnectRequest),
            ConnectReady msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.ConnectReady),
            HolePunchStart msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.HolePunchStart),
            P2pConnectMessage msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.P2pConnectMessage),
            P2pAcceptMessage msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.P2pAcceptMessage),
            RelayRequest msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.RelayRequest),
            RelayAccept msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.RelayAccept),
            RelayData msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.RelayData),
            ErrorMessage msg => JsonSerializer.SerializeToUtf8Bytes(msg, MessageJsonContext.Default.ErrorMessage),
            _ => JsonSerializer.SerializeToUtf8Bytes(message, MessageJsonContext.Default.BaseMessage)
        };
    }
    
    /// <summary>
    /// 序列化消息为 JSON 字符串
    /// </summary>
    public static string SerializeToString(BaseMessage message)
    {
        return message switch
        {
            RegisterMessage msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.RegisterMessage),
            RegisterAck msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.RegisterAck),
            ConnectRequest msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.ConnectRequest),
            ConnectReady msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.ConnectReady),
            HolePunchStart msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.HolePunchStart),
            P2pConnectMessage msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.P2pConnectMessage),
            P2pAcceptMessage msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.P2pAcceptMessage),
            RelayRequest msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.RelayRequest),
            RelayAccept msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.RelayAccept),
            RelayData msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.RelayData),
            ErrorMessage msg => JsonSerializer.Serialize(msg, MessageJsonContext.Default.ErrorMessage),
            _ => JsonSerializer.Serialize(message, MessageJsonContext.Default.BaseMessage)
        };
    }
    
    public static BaseMessage? Deserialize(byte[] data)
    {
        using var doc = JsonDocument.Parse(data);
        if (!doc.RootElement.TryGetProperty("type", out var typeProp))
        {
            return null;
        }
        var type = (MessageType)typeProp.GetByte();
        
        return type switch
        {
            MessageType.Register => JsonSerializer.Deserialize(data, MessageJsonContext.Default.RegisterMessage),
            MessageType.RegisterAck => JsonSerializer.Deserialize(data, MessageJsonContext.Default.RegisterAck),
            MessageType.ConnectRequest => JsonSerializer.Deserialize(data, MessageJsonContext.Default.ConnectRequest),
            MessageType.ConnectReady => JsonSerializer.Deserialize(data, MessageJsonContext.Default.ConnectReady),
            MessageType.HolePunchStart => JsonSerializer.Deserialize(data, MessageJsonContext.Default.HolePunchStart),
            MessageType.P2pConnect => JsonSerializer.Deserialize(data, MessageJsonContext.Default.P2pConnectMessage),
            MessageType.P2pAccept => JsonSerializer.Deserialize(data, MessageJsonContext.Default.P2pAcceptMessage),
            MessageType.RelayRequest => JsonSerializer.Deserialize(data, MessageJsonContext.Default.RelayRequest),
            MessageType.RelayAccept => JsonSerializer.Deserialize(data, MessageJsonContext.Default.RelayAccept),
            MessageType.RelayData => JsonSerializer.Deserialize(data, MessageJsonContext.Default.RelayData),
            MessageType.Error => JsonSerializer.Deserialize(data, MessageJsonContext.Default.ErrorMessage),
            _ => null
        };
    }
    
    /// <summary>
    /// 从 JSON 字符串反序列化
    /// </summary>
    public static BaseMessage? Deserialize(string json)
    {
        return Deserialize(System.Text.Encoding.UTF8.GetBytes(json));
    }
    
    /// <summary>
    /// 从 JSON 字符串反序列化为指定类型
    /// </summary>
    public static T? Deserialize<T>(string json) where T : BaseMessage
    {
        var typeInfo = MessageJsonContext.Default.GetTypeInfo(typeof(T)) as System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>;
        return typeInfo != null ? JsonSerializer.Deserialize(json, typeInfo) : null;
    }
    
    /// <summary>
    /// 从 JSON 字节反序列化为指定类型
    /// </summary>
    public static T? Deserialize<T>(byte[] data) where T : BaseMessage
    {
        var typeInfo = MessageJsonContext.Default.GetTypeInfo(typeof(T)) as System.Text.Json.Serialization.Metadata.JsonTypeInfo<T>;
        return typeInfo != null ? JsonSerializer.Deserialize(data, typeInfo) : null;
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false
)]
[JsonSerializable(typeof(BaseMessage))]
[JsonSerializable(typeof(RegisterMessage))]
[JsonSerializable(typeof(RegisterAck))]
[JsonSerializable(typeof(ConnectRequest))]
[JsonSerializable(typeof(ConnectReady))]
[JsonSerializable(typeof(HolePunchStart))]
[JsonSerializable(typeof(P2pConnectMessage))]
[JsonSerializable(typeof(P2pAcceptMessage))]
[JsonSerializable(typeof(RelayRequest))]
[JsonSerializable(typeof(RelayAccept))]
[JsonSerializable(typeof(RelayData))]
[JsonSerializable(typeof(ErrorMessage))]
[JsonSerializable(typeof(StunNatType))]
internal partial class MessageJsonContext : JsonSerializerContext
{
}
