using System.Collections.Concurrent;

namespace LanBridge.Common.Network;

/// <summary>
/// Priority frame queue for QoS scheduling.
/// High priority frames (control frames: Open, Close, Error) are dequeued first.
/// Normal and Low priority frames share a second queue with FIFO ordering.
/// </summary>
public enum FramePriority : byte
{
    High = 0,    // Control frames (Open, Close, Error)
    Normal = 1,  // Default for TCP mappings
    Low = 2      // Background / bulk transfers
}

internal sealed class PendingFrame
{
    public required byte[] Data { get; init; }
    public required int Offset { get; init; }
    public required int Length { get; init; }
    public required FramePriority Priority { get; init; }
}

public sealed class PriorityFrameQueue
{
    private readonly ConcurrentQueue<PendingFrame> _highQueue = new();
    private readonly ConcurrentQueue<PendingFrame> _normalQueue = new();

    public bool IsEmpty => _highQueue.IsEmpty && _normalQueue.IsEmpty;

    public void Enqueue(byte[] data, int offset, int length, FramePriority priority)
    {
        var frame = new PendingFrame
        {
            Data = data,
            Offset = offset,
            Length = length,
            Priority = priority
        };

        if (priority == FramePriority.High)
        {
            _highQueue.Enqueue(frame);
        }
        else
        {
            _normalQueue.Enqueue(frame);
        }
    }

    public bool TryDequeue(out byte[] data, out int offset, out int length)
    {
        // Strict priority: always dequeue from high queue first
        if (_highQueue.TryDequeue(out var frame))
        {
            data = frame.Data;
            offset = frame.Offset;
            length = frame.Length;
            return true;
        }

        if (_normalQueue.TryDequeue(out frame))
        {
            data = frame.Data;
            offset = frame.Offset;
            length = frame.Length;
            return true;
        }

        data = Array.Empty<byte>();
        offset = 0;
        length = 0;
        return false;
    }

    public int HighQueueCount => _highQueue.Count;
    public int NormalQueueCount => _normalQueue.Count;
}
