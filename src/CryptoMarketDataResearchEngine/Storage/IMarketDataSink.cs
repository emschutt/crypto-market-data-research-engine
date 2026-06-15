using CryptoMarketDataResearchEngine.Models;

namespace CryptoMarketDataResearchEngine.Storage;

public interface IMarketDataSink
{
    void Enqueue(RawDepthRow row);
    void Enqueue(RawAggTradeRow row);
    void Enqueue(BookChangeRow row);
    void Enqueue(FeatureRow row);
    void Enqueue(SnapshotRow row);
    Task<IReadOnlyDictionary<string, int>> FlushAsync(CancellationToken ct = default);
}
