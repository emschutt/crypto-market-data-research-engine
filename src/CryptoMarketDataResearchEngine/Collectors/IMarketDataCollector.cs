using CryptoMarketDataResearchEngine.Configuration;

namespace CryptoMarketDataResearchEngine.Collectors;

public interface IMarketDataCollector
{
    Task<CaptureResult> RunAsync(CancellationToken ct = default);
}
