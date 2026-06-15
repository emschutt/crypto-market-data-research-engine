using CryptoMarketDataResearchEngine.Diagnostics;

namespace CryptoMarketDataResearchEngine.Configuration;

public sealed record CaptureResult(
    string Mode,
    string Symbol,
    string OutputPath,
    IReadOnlyDictionary<string, int> RowsWritten,
    IReadOnlyList<LatencySummary> Latency,
    IReadOnlyList<QualityCheck> QualityChecks);
