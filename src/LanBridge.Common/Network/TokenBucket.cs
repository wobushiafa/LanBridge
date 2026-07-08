using System.Diagnostics;

namespace LanBridge.Common.Network;

/// <summary>
/// Zero-allocation token bucket rate limiter.
/// Uses monotonic Stopwatch for refill timing to avoid system clock jumps.
/// </summary>
public sealed class TokenBucket
{
    private readonly long _rate;              // bytes per second
    private readonly long _burstCapacity;     // max accumulated tokens
    private long _tokens;                     // current available tokens (fixed-point: multiplied by 1000)
    private long _lastRefillTimestamp;         // Stopwatch.GetTimestamp() at last refill
    private readonly SemaphoreSlim _waitSignal = new(1, 1);

    private const int Scale = 1000; // Fixed-point scale factor

    /// <summary>
    /// Current utilization: 0.0 (empty) to 1.0 (full).
    /// </summary>
    public double Utilization => _burstCapacity > 0 ? Math.Min(1.0, (double)_tokens / (_burstCapacity * Scale)) : 1.0;

    public long RateBytesPerSec => _rate;

    public TokenBucket(long rateBytesPerSec)
    {
        _rate = rateBytesPerSec;
        // Burst capacity: max 100ms of burst, at least one MTU (1500 bytes)
        _burstCapacity = Math.Max(rateBytesPerSec / 10, 1500);
        _tokens = _burstCapacity * Scale;
        _lastRefillTimestamp = Stopwatch.GetTimestamp();
    }

    /// <summary>
    /// Non-blocking attempt to consume tokens. Returns true if successful.
    /// </summary>
    public bool TryConsume(int length)
    {
        Refill();
        var cost = (long)length * Scale;
        if (_tokens >= cost)
        {
            _tokens -= cost;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Async wait until tokens are available, then consume.
    /// Uses a polling loop with adaptive delay based on deficit.
    /// </summary>
    public async Task WaitForTokensAsync(int length, CancellationToken ct)
    {
        var cost = (long)length * Scale;
        while (true)
        {
            Refill();
            if (_tokens >= cost)
            {
                _tokens -= cost;
                return;
            }

            // Calculate wait time based on deficit
            var deficit = cost - _tokens;
            var waitMs = (int)Math.Max(1, (deficit * 1000L) / (_rate * Scale));
            waitMs = Math.Min(waitMs, 1000); // Cap at 1 second

            try
            {
                await Task.Delay(waitMs, ct);
            }
            catch (OperationCanceledException)
            {
                // Timeout: discard and warn (per REQ-5)
                return;
            }
        }
    }

    private void Refill()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = now - _lastRefillTimestamp;
        if (elapsed <= 0) return;

        var elapsedNs = (long)((double)elapsed / Stopwatch.Frequency * 1_000_000_000);
        var added = (elapsedNs * _rate) / 1_000_000_000; // bytes added
        var addedScaled = added * Scale;

        var newTokens = _tokens + addedScaled;
        var maxTokens = _burstCapacity * Scale;
        _tokens = newTokens > maxTokens ? maxTokens : newTokens;
        _lastRefillTimestamp = now;
    }
}
