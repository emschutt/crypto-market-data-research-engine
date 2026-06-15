namespace CryptoMarketDataResearchEngine.Diagnostics;

public sealed class WebSocketLatencyTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSamples;

    public WebSocketLatencyTracker(int maxSamples = 10_000)
    {
        _maxSamples = maxSamples;
    }

    public double Record(string source, DateTime eventTs, DateTime? localReceiveTs = null)
    {
        var observed = localReceiveTs ?? DateTime.UtcNow;
        var lag = Math.Max((observed.ToUniversalTime() - eventTs.ToUniversalTime()).TotalMilliseconds, 0);

        lock (_gate)
        {
            if (!_samples.TryGetValue(source, out var queue))
            {
                queue = new Queue<double>();
                _samples[source] = queue;
            }

            queue.Enqueue(lag);
            while (queue.Count > _maxSamples)
                queue.Dequeue();
        }

        return lag;
    }

    public IReadOnlyList<LatencySummary> Summaries()
    {
        lock (_gate)
        {
            return _samples
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x =>
                {
                    var values = x.Value.ToArray();
                    return new LatencySummary(
                        Source: x.Key,
                        Samples: values.Length,
                        AverageMs: values.Length == 0 ? 0 : Math.Round(values.Average(), 3),
                        MinMs: values.Length == 0 ? 0 : Math.Round(values.Min(), 3),
                        MaxMs: values.Length == 0 ? 0 : Math.Round(values.Max(), 3));
                })
                .ToList();
        }
    }
}

public sealed record LatencySummary(
    string Source,
    int Samples,
    double AverageMs,
    double MinMs,
    double MaxMs);
