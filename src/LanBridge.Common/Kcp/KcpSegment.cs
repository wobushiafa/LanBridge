namespace LanBridge.Common.Kcp;

/// <summary>
/// KCP 数据段结构
/// </summary>
public class KcpSegment
{
    /// <summary>会话ID</summary>
    public uint Conv { get; set; }
    
    /// <summary>命令类型: PUSH/ACK/ASK/LAST</summary>
    public byte Cmd { get; set; }
    
    /// <summary>分片数量</summary>
    public byte Frg { get; set; }
    
    /// <summary>接收窗口大小</summary>
    public ushort Wnd { get; set; }
    
    /// <summary>时间戳</summary>
    public uint Ts { get; set; }
    
    /// <summary>序列号</summary>
    public uint Sn { get; set; }
    
    /// <summary>未确认序列号</summary>
    public uint Una { get; set; }
    
    /// <summary>数据长度</summary>
    public uint Len { get; set; }
    
    /// <summary>发送时间（用于重传计算）</summary>
    public uint Resendts { get; set; }
    
    /// <summary>重传超时</summary>
    public uint Rto { get; set; }
    
    /// <summary>快速重传触发次数</summary>
    public uint Fastack { get; set; }
    
    /// <summary>重传次数</summary>
    public uint Xmit { get; set; }
    
    /// <summary>负载数据</summary>
    public byte[] Data { get; set; } = Array.Empty<byte>();
    
    public bool IsRented { get; set; }
    
    public void Free()
    {
        if (IsRented && Data != null && Data != Array.Empty<byte>())
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(Data);
            Data = Array.Empty<byte>();
            IsRented = false;
        }
    }
    
    /// <summary>头部大小</summary>
    public const int HeaderSize = 24;
    
    /// <summary>命令常量</summary>
    public const byte CmdPush = 81;
    public const byte CmdAck = 82;
    public const byte CmdAsk = 83;  // WASK
    public const byte CmdTell = 84; // WINS
    
    /// <summary>
    /// 编码段头部到字节数组
    /// </summary>
    public int Encode(Span<byte> buf)
    {
        if (buf.Length < HeaderSize)
            throw new ArgumentException("Buffer too small for KCP header");
        
        int offset = 0;
        BinaryHelper.WriteUInt32(buf[offset..], Conv); offset += 4;
        buf[offset++] = Cmd;
        buf[offset++] = Frg;
        BinaryHelper.WriteUInt16(buf[offset..], Wnd); offset += 2;
        BinaryHelper.WriteUInt32(buf[offset..], Ts); offset += 4;
        BinaryHelper.WriteUInt32(buf[offset..], Sn); offset += 4;
        BinaryHelper.WriteUInt32(buf[offset..], Una); offset += 4;
        BinaryHelper.WriteUInt32(buf[offset..], Len); offset += 4;
        
        return HeaderSize;
    }
    
    /// <summary>
    /// 从字节数组解码头部
    /// </summary>
    public static KcpSegment Decode(ReadOnlySpan<byte> buf)
    {
        if (buf.Length < HeaderSize)
            throw new ArgumentException("Buffer too small for KCP header");
        
        int offset = 0;
        var seg = new KcpSegment();
        
        seg.Conv = BinaryHelper.ReadUInt32(buf[offset..]); offset += 4;
        seg.Cmd = buf[offset++];
        seg.Frg = buf[offset++];
        seg.Wnd = BinaryHelper.ReadUInt16(buf[offset..]); offset += 2;
        seg.Ts = BinaryHelper.ReadUInt32(buf[offset..]); offset += 4;
        seg.Sn = BinaryHelper.ReadUInt32(buf[offset..]); offset += 4;
        seg.Una = BinaryHelper.ReadUInt32(buf[offset..]); offset += 4;
        seg.Len = BinaryHelper.ReadUInt32(buf[offset..]);
        
        return seg;
    }
}

/// <summary>
/// 二进制读写辅助类
/// </summary>
public static class BinaryHelper
{
    public static void WriteUInt16(Span<byte> buf, ushort value)
    {
        buf[0] = (byte)(value & 0xFF);
        buf[1] = (byte)((value >> 8) & 0xFF);
    }
    
    public static ushort ReadUInt16(ReadOnlySpan<byte> buf)
    {
        return (ushort)(buf[0] | (buf[1] << 8));
    }
    
    public static void WriteUInt32(Span<byte> buf, uint value)
    {
        buf[0] = (byte)(value & 0xFF);
        buf[1] = (byte)((value >> 8) & 0xFF);
        buf[2] = (byte)((value >> 16) & 0xFF);
        buf[3] = (byte)((value >> 24) & 0xFF);
    }
    
    public static uint ReadUInt32(ReadOnlySpan<byte> buf)
    {
        return (uint)(buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24));
    }
}