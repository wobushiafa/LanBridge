using System.Collections.Concurrent;

namespace LanBridge.Common.Diagnostics;

public sealed class OperationalTelemetry
{
    private readonly ConcurrentDictionary<string, long> _counters = new(StringComparer.OrdinalIgnoreCase);

    public void Increment(string counterName, long delta = 1)
    {
        _counters.AddOrUpdate(counterName, delta, (_, current) => current + delta);
    }

    public IReadOnlyDictionary<string, long> Snapshot()
    {
        return new Dictionary<string, long>(_counters, StringComparer.OrdinalIgnoreCase);
    }

    public string FormatSnapshot()
    {
        var snapshot = Snapshot();
        if (snapshot.Count == 0)
        {
            return "no-metrics";
        }

        return string.Join(", ", snapshot.OrderBy(kvp => kvp.Key).Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }
}
