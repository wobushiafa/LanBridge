using System.Net;

#pragma warning disable CS0169, CS0414

namespace LanBridge.Common.Kcp;

/// <summary>
/// KCP 可靠 UDP 协议实现
/// </summary>
public class Kcp : IDisposable
{
    // 常量
    private const int RtoMin = 100;
    private const int RtoDef = 200;
    private const int RtoMax = 60000;
    private const int WndSnd = 1024;
    private const int WndRcv = 1024;
    private const int MtuDef = 1400;
    private const int Interval = 100;
    private const int Overhead = 24;
    private const int DeadLink = 20;
    
    // 状态
    private uint _conv;
    private uint _mtu = MtuDef;
    private uint _mss;
    private uint _state;
    private uint _sndUna;
    private uint _sndNxt;
    private uint _rcvNxt;
    private uint _tsRecent;
    private uint _tsLastAck;
    private uint _tsProbe;
    private uint _probeWait;
    private uint _sndWnd = WndSnd;
    private uint _rcvWnd = WndRcv;
    private uint _rmtWnd = WndRcv;
    private uint _cwnd;
    private uint _incr;
    private uint _probe;
    private uint _current;
    private uint _interval = Interval;
    private uint _tsFlush = Interval;
    private uint _nodelay;
    private uint _updated;
    private uint _logmask;
    private uint _ssthresh;
    private uint _rxRto = RtoDef;
    private uint _rxRttvar;
    private uint _rxSrtt;
    private uint _rxMinrto = RtoMin;
    private uint _fastresend;
    private uint _fastlimit = 5;
    private long _nocwnd;
    
    // Adaptive congestion control and RTT stats
    private uint _rttMin = uint.MaxValue;
    private uint _rttMax = 0;
    private bool _useAdaptiveCongestion = true;
    private bool _enableLegacyMode = false;
    
    // 队列
    private readonly Queue<KcpSegment> _sndQueue = new();
    private readonly Queue<KcpSegment> _rcvQueue = new();
    private readonly List<KcpSegment> _sndBuf = new();
    private readonly List<KcpSegment> _rcvBuf = new();
    private readonly List<(uint sn, uint ts)> _ackList = new();
    
    // 缓冲区
    private byte[] _buffer;
    private readonly byte[] _sndBufRaw;
    
    // 回调
    private readonly Action<byte[], int, IPEndPoint> _output;
    private readonly IPEndPoint? _remoteEp;
    
    // 统计
    private uint _xmit;
    private uint _deadLink = DeadLink;
    
    public uint Mtu => _mtu;
    public uint Mss => _mss;
    public uint State => _state;
    public uint Cwnd => _cwnd;
    public uint SndQueueCount => (uint)_sndQueue.Count;
    public uint RcvQueueCount => (uint)_rcvQueue.Count;
    
    public uint Srtt => _rxSrtt;
    public uint Rttvar => _rxRttvar;
    public uint Rto => _rxRto;
    
    public bool UseAdaptiveCongestion
    {
        get => _useAdaptiveCongestion;
        set => _useAdaptiveCongestion = value;
    }
    
    public bool EnableLegacyMode
    {
        get => _enableLegacyMode;
        set => _enableLegacyMode = value;
    }
    
    public Kcp(uint conv, Action<byte[], int, IPEndPoint> output, IPEndPoint? remoteEp = null)
    {
        _conv = conv;
        _output = output;
        _remoteEp = remoteEp;
        _mss = _mtu - Overhead;
        _buffer = new byte[(_mtu + Overhead) * 3];
        _sndBufRaw = new byte[_mtu];
        _sndWnd = WndSnd;
        _rcvWnd = WndRcv;
        _rmtWnd = WndRcv;
        _cwnd = 1;
        _ssthresh = WndSnd;
        _fastresend = 0;
    }
    
    /// <summary>
    /// 接收数据
    /// </summary>
    public int Receive(byte[] buffer, int offset, int length)
    {
        if (_rcvQueue.Count == 0)
            return -1;
        
        int peekSize = PeekSize();
        if (peekSize < 0)
            return -1;
            
        if (length < peekSize)
            return -2;
        
        int recover = _rcvQueue.Count >= _rcvWnd ? 1 : 0;
        
        // 合并分片
        int index = 0;
        int fragment = 0;
        
        while (_rcvQueue.Count > 0)
        {
            var seg = _rcvQueue.Peek();
            if (seg.Len > 0)
            {
                int copyLen = Math.Min((int)seg.Len, length - index);
                if (copyLen > 0)
                {
                    Array.Copy(seg.Data, 0, buffer, offset + index, copyLen);
                    index += copyLen;
                }
            }
            
            fragment = seg.Frg;
            _rcvQueue.Dequeue();
            seg.Free();
            
            if (fragment == 0)
                break;
        }
        
        // 移动到接收缓冲区
        MoveRcvBuf();
        
        // 快速恢复
        if (recover == 1 && _rcvQueue.Count < _rcvWnd)
        {
            _probe |= 1; // 发送窗口探测
        }
        
        return index;
    }
    
    /// <summary>
    /// 发送数据
    /// </summary>
    public int Send(byte[] data, int offset, int length)
    {
        if (length < 0 || data.Length < offset + length)
            return -1;
        
        int count;
        if (length <= (int)_mss)
        {
            count = 1;
        }
        else
        {
            count = (int)((length + _mss - 1) / _mss);
        }
        
        if (count > 255)
            return -2;
        
        if (count == 0)
            count = 1;
        
        // 分片发送
        for (int i = 0; i < count; i++)
        {
            int size = length > (int)_mss ? (int)_mss : length;
            var seg = new KcpSegment
            {
                Cmd = KcpSegment.CmdPush,
                Frg = (byte)(count - i - 1),
                Wnd = (ushort)Wnd(),
                Sn = 0,
                Una = _rcvNxt,
                Len = (uint)size,
                Data = System.Buffers.ArrayPool<byte>.Shared.Rent(size),
                IsRented = true
            };
            
            Array.Copy(data, offset + i * (int)_mss, seg.Data, 0, size);
            _sndQueue.Enqueue(seg);
            
            length -= size;
        }
        
        return 0;
    }
    
    /// <summary>
    /// 更新状态（定期调用）
    /// </summary>
    public void Update(uint current)
    {
        _current = current;
        
        if (_updated == 0)
        {
            _updated = 1;
            _tsFlush = _interval;
        }
        
        int slap = TimeDiff(_current, _tsFlush);
        
        if (slap >= 10000 || slap < -10000)
        {
            _tsFlush = _current;
            slap = 0;
        }
        
        if (slap >= 0)
        {
            _tsFlush += _interval;
            
            // 发送数据
            Flush();
            
            // 探测窗口
            if (_probe != 0)
            {
                ProbeWnd();
            }
        }
    }
    
    /// <summary>
    /// 接收原始数据
    /// </summary>
    public int Input(byte[] data, int offset, int size)
    {
        if (size < Overhead)
            return -1;
        
        uint maxUna = _sndUna + _sndWnd;
        int flag = 0;
        uint maxAck = 0;
        uint latestTs = 0;
        int offsetOld = offset;
        
        while (true)
        {
            if (size < Overhead)
                break;
            
            var seg = KcpSegment.Decode(data.AsSpan(offset));
            offset += Overhead;
            size -= Overhead;
            
            if (seg.Len > 0)
            {
                if (size < (int)seg.Len)
                    break;
                
                seg.Data = System.Buffers.ArrayPool<byte>.Shared.Rent((int)seg.Len);
                seg.IsRented = true;
                Array.Copy(data, offset, seg.Data, 0, (int)seg.Len);
                offset += (int)seg.Len;
                size -= (int)seg.Len;
            }
            
            if (seg.Conv != _conv)
            {
                seg.Free();
                return -1;
            }
            
            _rmtWnd = seg.Wnd;
            
            // 更新发送队列
            var previousUna = _sndUna;
            ParseUna(seg.Una);
            ShrinkBuf();
            if (_sndUna != previousUna)
            {
                UpdateCongestionWindow();
            }
            
            switch (seg.Cmd)
            {
                case KcpSegment.CmdAck:
                    int rtt = TimeDiff(_current, seg.Ts);
                    if (rtt >= 0)
                    {
                        UpdateAck(rtt);
                        if ((uint)rtt < _rttMin) _rttMin = (uint)rtt;
                        if ((uint)rtt > _rttMax) _rttMax = (uint)rtt;
                    }
                    ParseAck(seg.Sn);
                    ShrinkBuf();
                    if (flag == 0)
                    {
                        flag = 1;
                        _tsRecent = seg.Ts;
                        maxAck = seg.Sn;
                        latestTs = seg.Ts;
                    }
                    else if (TimeDiff(seg.Sn, maxAck) > 0)
                    {
                        maxAck = seg.Sn;
                        latestTs = seg.Ts;
                    }
                    seg.Free();
                    break;
                
                case KcpSegment.CmdPush:
                    if (TimeDiff(seg.Sn, _rcvWnd + _rcvNxt) < 0)
                    {
                        _ackList.Add((seg.Sn, seg.Ts));
                        
                        if (TimeDiff(seg.Sn, _rcvNxt) >= 0)
                        {
                            ParseData(seg);
                        }
                        else
                        {
                            seg.Free();
                        }
                    }
                    else
                    {
                        seg.Free();
                    }
                    break;
                
                case KcpSegment.CmdAsk:
                    // 窗口探测
                    _probe |= 2;
                    seg.Free();
                    break;
                
                case KcpSegment.CmdTell:
                    // 窗口通告
                    seg.Free();
                    break;
                
                default:
                    seg.Free();
                    return -1;
            }
        }
        
        if (flag != 0)
        {
            ParseFastack(maxAck);
        }
        
        return offset - offsetOld;
    }
    
    /// <summary>
    /// 等待接收数据大小
    /// </summary>
    public int PeekSize()
    {
        if (_rcvQueue.Count == 0)
            return -1;
        
        int length = 0;
        bool hasEnd = false;
        foreach (var seg in _rcvQueue)
        {
            length += (int)seg.Len;
            if (seg.Frg == 0)
            {
                hasEnd = true;
                break;
            }
        }
        
        if (!hasEnd)
            return -1;
        
        return length;
    }
    
    /// <summary>
    /// 获取接收窗口大小
    /// </summary>
    public uint Wnd()
    {
        uint remaining = _rcvWnd - (uint)_rcvQueue.Count;
        return remaining > 0 ? remaining : 0;
    }
    
    /// <summary>
    /// 设置无延迟模式
    /// </summary>
    public void SetNodelay(uint nodelay, uint interval, int resend, int nc)
    {
        if (nodelay > 0)
        {
            _nodelay = nodelay;
            _rxMinrto = 30;
        }
        else
        {
            _nodelay = 0;
            _rxMinrto = RtoMin;
        }
        
        if (interval >= 10)
            _interval = (uint)interval;
        else
            _interval = 10;
        
        if (resend >= 0)
            _fastresend = (uint)resend;
        
        if (nc >= 0)
            _nocwnd = nc;
    }

    public int SetMtu(uint mtu)
    {
        if (mtu < 576 || mtu <= Overhead)
        {
            return -1;
        }

        _mtu = mtu;
        _mss = _mtu - Overhead;
        if (_buffer.Length < (_mtu + Overhead) * 3)
        {
            _buffer = new byte[(_mtu + Overhead) * 3];
        }

        return 0;
    }
    
    /// <summary>
    /// 设置最大窗口
    /// </summary>
    public void WndSize(uint sndwnd, uint rcvwnd)
    {
        if (sndwnd > 0)
            _sndWnd = sndwnd;
        
        if (rcvwnd > 0)
            _rcvWnd = rcvwnd;
    }
    
    /// <summary>
    /// 等待发送队列大小
    /// </summary>
    public int WaitSnd()
    {
        return _sndBuf.Count + _sndQueue.Count;
    }
    
    /// <summary>
    /// 刷新发送
    /// </summary>
    private void Flush()
    {
        // 发送 ACK
        foreach (var (sn, ts) in _ackList)
        {
            var seg = new KcpSegment
            {
                Cmd = KcpSegment.CmdAck,
                Sn = sn,
                Ts = ts,
                Una = _rcvNxt,
                Wnd = (ushort)Wnd()
            };
            SendSegment(seg);
        }
        _ackList.Clear();
        
        // 探测窗口
        if (_rmtWnd == 0)
        {
            if (TimeDiff(_current, _tsProbe) >= 0)
            {
                if (_probeWait == 0)
                {
                    _probeWait = 7000;
                    _tsProbe = _current + _probeWait;
                }
                else
                {
                    _probeWait += _probeWait / 2;
                    if (_probeWait > 7000)
                        _probeWait = 7000;
                    _tsProbe = _current + _probeWait;
                }
                _probe |= 2;
            }
        }
        else
        {
            _tsProbe = 0;
            _probeWait = 0;
        }
        
        // 发送探测命令
        if ((_probe & 1) != 0)
        {
            var seg = new KcpSegment
            {
                Cmd = KcpSegment.CmdAsk,
                Una = _rcvNxt,
                Wnd = (ushort)Wnd()
            };
            SendSegment(seg);
            _probe &= ~1u;
        }
        
        if ((_probe & 2) != 0)
        {
            var seg = new KcpSegment
            {
                Cmd = KcpSegment.CmdTell,
                Una = _rcvNxt,
                Wnd = (ushort)Wnd()
            };
            SendSegment(seg);
            _probe &= ~2u;
        }
        
        // 发送数据
        uint cwnd = Math.Min(_sndWnd, _rmtWnd);
        if (_nocwnd == 0)
            cwnd = Math.Min(_cwnd, cwnd);
        
        while (TimeDiff(_sndNxt, _sndUna + cwnd) < 0)
        {
            if (_sndQueue.Count == 0)
                break;
            
            var seg = _sndQueue.Dequeue();
            seg.Sn = _sndNxt++;
            seg.Ts = _current;
            seg.Resendts = _current;
            seg.Rto = _rxRto;
            seg.Fastack = 0;
            seg.Xmit = 0;
            
            _sndBuf.Add(seg);
        }
        
        // 重传检测
        uint resent = _fastresend > 0 ? _fastresend : 0xffffffff;
        uint rtomin = _nodelay == 0 ? (_rxRto >> 3) : 0;
        
        bool lost = false;
        bool change = false;
        
        for (int i = 0; i < _sndBuf.Count; i++)
        {
            var seg = _sndBuf[i];
            int needsend = 0;
            
            if (seg.Xmit == 0)
            {
                // 首次发送
                needsend = 1;
                seg.Xmit++;
                seg.Rto = _rxRto;
                seg.Resendts = _current + seg.Rto + rtomin;
            }
            else if (TimeDiff(_current, seg.Resendts) >= 0)
            {
                // 超时重传
                needsend = 1;
                seg.Xmit++;
                
                if (_nodelay == 0)
                    seg.Rto += Math.Max(seg.Rto, (uint)(_rxRto * 3 / 2));
                else
                    seg.Rto += _rxRto / 2;

                lost = true;
                
                seg.Resendts = _current + seg.Rto;
                _state = 0; // 重置状态
            }
            else if (seg.Fastack >= resent)
            {
                // 快速重传
                needsend = 1;
                seg.Xmit++;
                seg.Fastack = 0;
                seg.Resendts = _current + seg.Rto;
                change = true;
            }
            
            if (needsend > 0)
            {
                seg.Ts = _current;
                seg.Wnd = (ushort)Wnd();
                seg.Una = _rcvNxt;
                
                SendSegment(seg);
                
                if (seg.Xmit >= _deadLink)
                    _state = 1; // 连接断开
            }
        }
        
        // 拥塞窗口决策
        if (_nocwnd == 0)
        {
            if (_enableLegacyMode)
            {
                if (lost || change)
                {
                    _ssthresh = Math.Max(_cwnd / 2, 2);
                    _cwnd = 1;
                }
            }
            else
            {
                if (lost)
                {
                    _ssthresh = Math.Max(_cwnd / 2, 2);
                    _cwnd = 1;
                }
                else if (change)
                {
                    uint fastResendVal = _fastresend > 0 ? _fastresend : 2;
                    if (_useAdaptiveCongestion && _rttMax > _rttMin)
                    {
                        double queueDelaySeverity = (double)(_rxSrtt - _rttMin) / (_rttMax - _rttMin);
                        if (queueDelaySeverity < 0.3)
                        {
                            _ssthresh = Math.Max((uint)(_cwnd * 0.8), 2);
                        }
                        else
                        {
                            _ssthresh = Math.Max(_cwnd / 2, 2);
                        }
                    }
                    else
                    {
                        _ssthresh = Math.Max(_cwnd / 2, 2);
                    }
                    _cwnd = _ssthresh + fastResendVal;
                }
            }
            
            if (_cwnd > _rmtWnd)
            {
                _cwnd = _rmtWnd;
            }
        }
    }
    
    /// <summary>
    /// 发送单个段
    /// </summary>
    private void SendSegment(KcpSegment seg)
    {
        seg.Conv = _conv;
        int size = seg.Len > 0 ? (int)seg.Len + Overhead : Overhead;
        
        if (size > _buffer.Length)
        {
            _buffer = new byte[size];
        }
        
        seg.Encode(_buffer);
        
        if (seg.Len > 0)
        {
            Array.Copy(seg.Data, 0, _buffer, Overhead, (int)seg.Len);
        }
        
        _output(_buffer, size, _remoteEp!);
        _xmit++;
    }
    
    /// <summary>
    /// 解析 ACK
    /// </summary>
    private void ParseAck(uint sn)
    {
        if (TimeDiff(sn, _sndUna) < 0 || TimeDiff(sn, _sndNxt) >= 0)
            return;
        
        for (int i = 0; i < _sndBuf.Count; i++)
        {
            var seg = _sndBuf[i];
            if (sn == seg.Sn)
            {
                _sndBuf.RemoveAt(i);
                seg.Free();
                break;
            }
            
            if (TimeDiff(sn, seg.Sn) < 0)
                break;
        }
    }
    
    /// <summary>
    /// 解析 UNA
    /// </summary>
    private void ParseUna(uint una)
    {
        int count = 0;
        foreach (var seg in _sndBuf)
        {
            if (TimeDiff(una, seg.Sn) > 0)
                count++;
            else
                break;
        }
        
        if (count > 0)
        {
            for (int i = 0; i < count; i++)
            {
                _sndBuf[i].Free();
            }
            _sndBuf.RemoveRange(0, count);
        }
    }
    
    /// <summary>
    /// 解析数据
    /// </summary>
    private void ParseData(KcpSegment newseg)
    {
        uint sn = newseg.Sn;
        
        if (TimeDiff(sn, _rcvNxt + _rcvWnd) >= 0 || TimeDiff(sn, _rcvNxt) < 0)
        {
            newseg.Free();
            return;
        }
        
        // 插入到接收缓冲区（有序）
        int insertIndex = _rcvBuf.Count;
        for (int i = _rcvBuf.Count - 1; i >= 0; i--)
        {
            var seg = _rcvBuf[i];
            if (seg.Sn == sn)
            {
                newseg.Free(); // 重复包
                return;
            }
            
            if (TimeDiff(sn, seg.Sn) > 0)
            {
                insertIndex = i + 1;
                break;
            }
            insertIndex = i;
        }
        
        _rcvBuf.Insert(insertIndex, newseg);
        
        // 移动到接收队列
        MoveRcvBuf();
    }
    
    /// <summary>
    /// 移动接收缓冲区到接收队列
    /// </summary>
    private void MoveRcvBuf()
    {
        while (_rcvBuf.Count > 0)
        {
            var seg = _rcvBuf[0];
            if (seg.Sn == _rcvNxt && _rcvQueue.Count < _rcvWnd)
            {
                _rcvBuf.RemoveAt(0);
                _rcvQueue.Enqueue(seg);
                _rcvNxt++;
            }
            else
            {
                break;
            }
        }
    }
    
    /// <summary>
    /// 收缩发送缓冲区
    /// </summary>
    private void ShrinkBuf()
    {
        if (_sndBuf.Count > 0)
        {
            _sndUna = _sndBuf[0].Sn;
        }
        else
        {
            _sndUna = _sndNxt;
        }
    }

    private void UpdateCongestionWindow()
    {
        if (_nocwnd != 0 || _rmtWnd == 0)
        {
            return;
        }

        if (_cwnd < _ssthresh)
        {
            _cwnd++;
        }
        else if (_cwnd < _rmtWnd)
        {
            _cwnd += Math.Max(1, _cwnd / 8);
        }

        if (_cwnd > _rmtWnd)
        {
            _cwnd = _rmtWnd;
        }
    }

    private void UpdateAck(int rtt)
    {
        if (_enableLegacyMode)
        {
            return;
        }
        if (_rxSrtt == 0)
        {
            _rxSrtt = (uint)rtt;
            _rxRttvar = (uint)rtt / 2;
        }
        else
        {
            int delta = rtt - (int)_rxSrtt;
            if (delta < 0) delta = -delta;
            _rxRttvar = (3 * _rxRttvar + (uint)delta) / 4;
            _rxSrtt = (7 * _rxSrtt + (uint)rtt) / 8;
            if (_rxSrtt < 1) _rxSrtt = 1;
        }
        uint rto = _rxSrtt + Math.Max(_interval, 4 * _rxRttvar);
        
        uint dynamicMinRto = Math.Clamp(_rxSrtt + 3 * _rxRttvar, _rxMinrto, 300u);
        _rxRto = Math.Clamp(rto, dynamicMinRto, RtoMax);
    }

    private void ParseFastack(uint sn)
    {
        if (_enableLegacyMode)
        {
            return;
        }
        if (TimeDiff(sn, _sndUna) < 0 || TimeDiff(sn, _sndNxt) >= 0)
            return;

        for (int i = 0; i < _sndBuf.Count; i++)
        {
            var seg = _sndBuf[i];
            if (TimeDiff(sn, seg.Sn) < 0)
                break;
            
            if (sn != seg.Sn)
            {
                seg.Fastack++;
            }
        }
    }
    
    /// <summary>
    /// 窗口探测
    /// </summary>
    private void ProbeWnd()
    {
        // 已在 Flush 中处理
    }
    
    /// <summary>
    /// 时间差计算
    /// </summary>
    private static int TimeDiff(uint later, uint earlier)
    {
        return (int)(later - earlier);
    }
    
    public void Dispose()
    {
        while (_sndQueue.TryDequeue(out var seg)) seg.Free();
        while (_rcvQueue.TryDequeue(out var seg)) seg.Free();
        foreach (var seg in _sndBuf) seg.Free();
        _sndBuf.Clear();
        foreach (var seg in _rcvBuf) seg.Free();
        _rcvBuf.Clear();
    }
}

#pragma warning restore CS0169, CS0414
