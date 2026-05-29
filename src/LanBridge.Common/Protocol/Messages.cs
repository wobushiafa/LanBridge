using System.Net;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    Error = 6
}

public sealed class TunnelFrame
{
    private const uint Magic = 0x31503250; // P2P1
    private const byte Version = 1;
    private const int HeaderSize = 16;
    private const int MaxPayloadLength = 16 * 1024 * 1024;

    public TunnelFrameType Type { get; init; }
    public uint StreamId { get; init; }
    public byte[] Payload { get; init; } = Array.Empty<byte>();

    public static TunnelFrame Data(uint streamId, byte[] payload, int offset, int length)
    {
        var copy = new byte[length];
        Buffer.BlockCopy(payload, offset, copy, 0, length);
        return new TunnelFrame { Type = TunnelFrameType.Data, StreamId = streamId, Payload = copy };
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
        return Payload.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Payload);
    }

    public byte[] Encode()
    {
        if (Payload.Length > MaxPayloadLength)
        {
            throw new InvalidDataException($"Tunnel frame payload too large: {Payload.Length}");
        }

        var buffer = new byte[HeaderSize + Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), Magic);
        buffer[4] = Version;
        buffer[5] = (byte)Type;
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6, 2), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(8, 4), StreamId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(12, 4), Payload.Length);
        Buffer.BlockCopy(Payload, 0, buffer, HeaderSize, Payload.Length);
        return buffer;
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

        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            Buffer.BlockCopy(data, HeaderSize, payload, 0, payloadLength);
        }

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
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };
    
    /// <summary>
    /// 序列化消息为 JSON 字节
    /// </summary>
    public static byte[] Serialize(BaseMessage message)
    {
        return JsonSerializer.SerializeToUtf8Bytes(message, message.GetType(), Options);
    }
    
    /// <summary>
    /// 序列化消息为 JSON 字符串
    /// </summary>
    public static string SerializeToString(BaseMessage message)
    {
        return JsonSerializer.Serialize(message, message.GetType(), Options);
    }
    
    public static BaseMessage? Deserialize(byte[] data)
    {
        var doc = JsonDocument.Parse(data);
        var typeProp = doc.RootElement.GetProperty("type");
        var type = (MessageType)typeProp.GetByte();
        
        return type switch
        {
            MessageType.Register => JsonSerializer.Deserialize<RegisterMessage>(data, Options),
            MessageType.RegisterAck => JsonSerializer.Deserialize<RegisterAck>(data, Options),
            MessageType.ConnectRequest => JsonSerializer.Deserialize<ConnectRequest>(data, Options),
            MessageType.ConnectReady => JsonSerializer.Deserialize<ConnectReady>(data, Options),
            MessageType.HolePunchStart => JsonSerializer.Deserialize<HolePunchStart>(data, Options),
            MessageType.P2pConnect => JsonSerializer.Deserialize<P2pConnectMessage>(data, Options),
            MessageType.P2pAccept => JsonSerializer.Deserialize<P2pAcceptMessage>(data, Options),
            MessageType.RelayRequest => JsonSerializer.Deserialize<RelayRequest>(data, Options),
            MessageType.RelayAccept => JsonSerializer.Deserialize<RelayAccept>(data, Options),
            MessageType.RelayData => JsonSerializer.Deserialize<RelayData>(data, Options),
            MessageType.Error => JsonSerializer.Deserialize<ErrorMessage>(data, Options),
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
        return JsonSerializer.Deserialize<T>(json, Options);
    }
    
    /// <summary>
    /// 从 JSON 字节反序列化为指定类型
    /// </summary>
    public static T? Deserialize<T>(byte[] data) where T : BaseMessage
    {
        return JsonSerializer.Deserialize<T>(data, Options);
    }
}
