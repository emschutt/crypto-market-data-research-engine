using System.Text.Json;
using CryptoMarketDataResearchEngine.Collectors;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Diagnostics;
using CryptoMarketDataResearchEngine.Export;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;

namespace CryptoMarketDataResearchEngine;

public static class PipelineRunner
{
    public static async Task<CaptureResult> RunAsync(CaptureOptions options, CancellationToken ct = default)
    {
        if (Directory.Exists(options.OutputPath) && options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase))
            Directory.Delete(options.OutputPath, recursive: true);

        var latency = new WebSocketLatencyTracker();
        var writer = new ParquetMarketDataWriter(options);
        var sink = new SampledMarketDataSink(writer);
        IMarketDataCollector collector = options.Mode.Equals("live", StringComparison.OrdinalIgnoreCase)
            ? new BinanceWebSocketCollector(options, sink, latency)
            : new MockBinanceCollector(options, sink, latency);

        var result = await collector.RunAsync(ct);
        DashboardSvgRenderer.Render(options, result.RowsWritten, sink.Snapshot(), result.Latency, "charts");
        return result;
    }

    public static async Task SmokeAsync(CancellationToken ct = default)
    {
        var options = new CaptureOptions
        {
            Mode = "mock",
            Symbol = "BTCUSDT",
            OutputPath = "sample_data/smoke",
            DatasetType = "all",
            CaptureDurationSeconds = 3,
            FeatureIntervalMs = 0,
            RollingWindowMs = 1000,
            MockEventsPerSecond = 100
        };

        var result = await RunAsync(options, ct);
        Require(result.RowsWritten.GetValueOrDefault(Datasets.RawDepth) >= 100, "raw_depth did not write enough rows");
        Require(result.RowsWritten.GetValueOrDefault(Datasets.RawAggTrades) >= 100, "raw_agg_trades did not write enough rows");
        Require(result.RowsWritten.GetValueOrDefault(Datasets.BookChangeEvents) >= 100, "book_change_events did not write enough rows");
        Require(result.RowsWritten.GetValueOrDefault(Datasets.Features) >= 100, "features did not write enough rows");
        Require(result.RowsWritten.GetValueOrDefault(Datasets.Snapshots) >= 1, "snapshots did not write");

        var inspections = new Dictionary<string, DatasetReadback>();
        foreach (var dataset in Datasets.All)
        {
            var inspection = await ParquetDatasetInspector.InspectAsync(options.OutputPath, dataset, ct);
            Require(inspection.FileCount > 0, $"{dataset} has no parquet files");
            Require(inspection.RowGroupCount > 0, $"{dataset} has no row groups");
            inspections[dataset] = inspection;
        }

        RequireColumns(inspections[Datasets.RawAggTrades], "event_ts_ms", "price", "quantity", "trade_side", "receive_latency_ms");
        RequireColumns(inspections[Datasets.RawDepth], "event_ts_ms", "first_update_id", "last_update_id", "bid_updates_json", "ask_updates_json");
        RequireColumns(inspections[Datasets.BookChangeEvents], "event_ts_ms", "event_type", "side", "price", "delta_quantity");
        RequireColumns(inspections[Datasets.Features], "event_ts_ms", "midprice", "spread", "order_flow_imbalance", "trade_imbalance");
        RequireColumns(inspections[Datasets.Snapshots], "event_ts_ms", "last_update_id", "bids_json", "asks_json");

        Console.WriteLine("SMOKE TEST PASSED");
        Console.WriteLine($"output_path={Path.GetFullPath(options.OutputPath)}");
        Console.WriteLine($"rows_written={JsonSerializer.Serialize(result.RowsWritten)}");
        Console.WriteLine($"chart={Path.GetFullPath(Path.Combine("charts", "market-data-pipeline-dashboard.svg"))}");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireColumns(DatasetReadback readback, params string[] columns)
    {
        foreach (var column in columns)
        {
            if (!readback.Columns.Contains(column, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{readback.Dataset} is missing column '{column}'");
        }
    }
}
