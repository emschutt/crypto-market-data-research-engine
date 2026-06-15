using CryptoMarketDataResearchEngine.Models;

namespace CryptoMarketDataResearchEngine.Configuration;

public sealed class CaptureOptions
{
    public string Symbol { get; init; } = "BTCUSDT";
    public string Mode { get; init; } = "mock";
    public string OutputPath { get; init; } = "sample_data/smoke";
    public string DatasetType { get; init; } = "all";
    public int CaptureDurationSeconds { get; init; } = 3;
    public int RestDepthLimit { get; init; } = 1000;
    public int FeatureIntervalMs { get; init; } = 0;
    public int RollingWindowMs { get; init; } = 1000;
    public bool RawPayload { get; init; }
    public int MockEventsPerSecond { get; init; } = 100;

    public string SymbolUpper => Symbol.Trim().ToUpperInvariant();
    public string SymbolLower => Symbol.Trim().ToLowerInvariant();
    public bool IsAllDatasets => DatasetType.Equals("all", StringComparison.OrdinalIgnoreCase);

    public bool ShouldWrite(string dataset) =>
        IsAllDatasets || DatasetType.Equals(dataset, StringComparison.OrdinalIgnoreCase);

    public static CaptureOptions FromArgs(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Count; i++)
        {
            var token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal)) continue;

            var key = token[2..];
            var value = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++i]
                : "true";
            values[key] = value;
        }

        var dataset = Get(values, "dataset", GetEnv("DATASET_TYPE", "all"));
        if (!dataset.Equals("all", StringComparison.OrdinalIgnoreCase) &&
            !Datasets.All.Contains(dataset, StringComparer.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Unknown dataset '{dataset}'. Use one of: all, {string.Join(", ", Datasets.All)}.");
        }

        return new CaptureOptions
        {
            Symbol = Get(values, "symbol", GetEnv("SYMBOL", "BTCUSDT")),
            Mode = Get(values, "mode", GetEnv("MODE", "mock")),
            OutputPath = Get(values, "output", GetEnv("OUTPUT_PATH", "sample_data/smoke")),
            DatasetType = dataset,
            CaptureDurationSeconds = GetInt(values, "duration", GetEnvInt("CAPTURE_DURATION_SECONDS", 3)),
            RestDepthLimit = GetInt(values, "rest-depth-limit", GetEnvInt("REST_DEPTH_LIMIT", 1000)),
            FeatureIntervalMs = GetInt(values, "feature-interval-ms", GetEnvInt("FEATURE_INTERVAL_MS", 0)),
            RollingWindowMs = GetInt(values, "rolling-window-ms", GetEnvInt("ROLLING_WINDOW_MS", 1000)),
            RawPayload = GetBool(values, "raw-payload", GetEnvBool("RAW_PAYLOAD", false)),
            MockEventsPerSecond = GetInt(values, "mock-events-per-second", GetEnvInt("MOCK_EVENTS_PER_SECOND", 100)),
        };
    }

    private static string Get(IReadOnlyDictionary<string, string> values, string key, string fallback) =>
        values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value : fallback;

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key, int fallback) =>
        values.TryGetValue(key, out var value) && int.TryParse(value, out var parsed) ? parsed : fallback;

    private static bool GetBool(IReadOnlyDictionary<string, string> values, string key, bool fallback) =>
        values.TryGetValue(key, out var value) && bool.TryParse(value, out var parsed) ? parsed : fallback;

    private static string GetEnv(string key, string fallback) =>
        Environment.GetEnvironmentVariable(key) is { Length: > 0 } value ? value : fallback;

    private static int GetEnvInt(string key, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(key), out var parsed) ? parsed : fallback;

    private static bool GetEnvBool(string key, bool fallback) =>
        bool.TryParse(Environment.GetEnvironmentVariable(key), out var parsed) ? parsed : fallback;
}
