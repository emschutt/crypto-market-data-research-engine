using CryptoMarketDataResearchEngine.Models;

namespace CryptoMarketDataResearchEngine.Storage;

public sealed class SampledMarketDataSink : IMarketDataSink
{
    private readonly IMarketDataSink _inner;
    private readonly int _maxRowsPerDataset;
    private readonly object _gate = new();
    private readonly List<RawDepthRow> _depth = [];
    private readonly List<RawAggTradeRow> _trades = [];
    private readonly List<BookChangeRow> _changes = [];
    private readonly List<FeatureRow> _features = [];
    private readonly List<SnapshotRow> _snapshots = [];

    public SampledMarketDataSink(IMarketDataSink inner, int maxRowsPerDataset = 5_000)
    {
        _inner = inner;
        _maxRowsPerDataset = maxRowsPerDataset;
    }

    public MarketDataSample Snapshot()
    {
        lock (_gate)
        {
            return new MarketDataSample(
                _depth.ToArray(),
                _trades.ToArray(),
                _changes.ToArray(),
                _features.ToArray(),
                _snapshots.ToArray());
        }
    }

    public void Enqueue(RawDepthRow row)
    {
        Remember(_depth, row);
        _inner.Enqueue(row);
    }

    public void Enqueue(RawAggTradeRow row)
    {
        Remember(_trades, row);
        _inner.Enqueue(row);
    }

    public void Enqueue(BookChangeRow row)
    {
        Remember(_changes, row);
        _inner.Enqueue(row);
    }

    public void Enqueue(FeatureRow row)
    {
        Remember(_features, row);
        _inner.Enqueue(row);
    }

    public void Enqueue(SnapshotRow row)
    {
        Remember(_snapshots, row);
        _inner.Enqueue(row);
    }

    public Task<IReadOnlyDictionary<string, int>> FlushAsync(CancellationToken ct = default) =>
        _inner.FlushAsync(ct);

    private void Remember<T>(List<T> rows, T row)
    {
        lock (_gate)
        {
            if (rows.Count < _maxRowsPerDataset)
                rows.Add(row);
        }
    }
}

public sealed record MarketDataSample(
    IReadOnlyList<RawDepthRow> Depth,
    IReadOnlyList<RawAggTradeRow> Trades,
    IReadOnlyList<BookChangeRow> Changes,
    IReadOnlyList<FeatureRow> Features,
    IReadOnlyList<SnapshotRow> Snapshots);
