using System.Collections.Concurrent;
using System.Text.Json;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Models;
using Parquet;
using Parquet.Data;
using Parquet.Schema;

namespace CryptoMarketDataResearchEngine.Storage;

public sealed class ParquetMarketDataWriter : IMarketDataSink
{
    private readonly CaptureOptions _options;
    private readonly string _rootPath;
    private readonly ConcurrentQueue<RawDepthRow> _depth = new();
    private readonly ConcurrentQueue<RawAggTradeRow> _trades = new();
    private readonly ConcurrentQueue<BookChangeRow> _changes = new();
    private readonly ConcurrentQueue<FeatureRow> _features = new();
    private readonly ConcurrentQueue<SnapshotRow> _snapshots = new();

    public ParquetMarketDataWriter(CaptureOptions options)
    {
        _options = options;
        _rootPath = Path.GetFullPath(options.OutputPath);
        Directory.CreateDirectory(_rootPath);
    }

    public void Enqueue(RawDepthRow row)
    {
        if (_options.ShouldWrite(Datasets.RawDepth)) _depth.Enqueue(row);
    }

    public void Enqueue(RawAggTradeRow row)
    {
        if (_options.ShouldWrite(Datasets.RawAggTrades)) _trades.Enqueue(row);
    }

    public void Enqueue(BookChangeRow row)
    {
        if (_options.ShouldWrite(Datasets.BookChangeEvents)) _changes.Enqueue(row);
    }

    public void Enqueue(FeatureRow row)
    {
        if (_options.ShouldWrite(Datasets.Features)) _features.Enqueue(row);
    }

    public void Enqueue(SnapshotRow row)
    {
        if (_options.ShouldWrite(Datasets.Snapshots)) _snapshots.Enqueue(row);
    }

    public async Task<IReadOnlyDictionary<string, int>> FlushAsync(CancellationToken ct = default)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        await DrainAndWriteAsync(Datasets.RawDepth, _depth, WriteRawDepthAsync, counts, ct);
        await DrainAndWriteAsync(Datasets.RawAggTrades, _trades, WriteRawAggTradesAsync, counts, ct);
        await DrainAndWriteAsync(Datasets.BookChangeEvents, _changes, WriteBookChangesAsync, counts, ct);
        await DrainAndWriteAsync(Datasets.Features, _features, WriteFeaturesAsync, counts, ct);
        await DrainAndWriteAsync(Datasets.Snapshots, _snapshots, WriteSnapshotsAsync, counts, ct);

        return counts;
    }

    private async Task DrainAndWriteAsync<T>(
        string dataset,
        ConcurrentQueue<T> queue,
        Func<IReadOnlyList<T>, string, CancellationToken, Task> writer,
        IDictionary<string, int> counts,
        CancellationToken ct)
    {
        var rows = new List<T>();
        while (queue.TryDequeue(out var row))
            rows.Add(row);

        if (rows.Count == 0)
            return;

        counts[dataset] = rows.Count;
        foreach (var group in rows.GroupBy(row => HourBucket(GetTimestamp(row))).OrderBy(x => x.Key))
        {
            var filePath = NextPartPath(dataset, group.Key);
            await writer(group.ToList(), filePath, ct);
            WriteMetadata(dataset, filePath, group.Count(), group.Key);
        }
    }

    private async Task WriteRawDepthAsync(IReadOnlyList<RawDepthRow> rows, string filePath, CancellationToken ct)
    {
        var eventTsMs = new DataField<long>("event_ts_ms");
        var eventTsIso = new DataField<string>("event_ts_iso");
        var localReceiveTsMs = new DataField<long>("local_receive_ts_ms");
        var symbol = new DataField<string>("symbol");
        var exchange = new DataField<string>("exchange");
        var firstUpdateId = new DataField<long>("first_update_id");
        var lastUpdateId = new DataField<long>("last_update_id");
        var bidUpdatesJson = new DataField<string>("bid_updates_json");
        var askUpdatesJson = new DataField<string>("ask_updates_json");
        var bidUpdateCount = new DataField<int>("bid_update_count");
        var askUpdateCount = new DataField<int>("ask_update_count");
        var receiveLatencyMs = new DataField<double>("receive_latency_ms");
        var rawPayloadJson = new DataField<string>("raw_payload_json");

        var schema = new ParquetSchema(eventTsMs, eventTsIso, localReceiveTsMs, symbol, exchange, firstUpdateId,
            lastUpdateId, bidUpdatesJson, askUpdatesJson, bidUpdateCount, askUpdateCount, receiveLatencyMs, rawPayloadJson);

        await WriteColumnsAsync(filePath, schema,
        [
            Col(eventTsMs, rows, x => ToUnixMs(x.EventTs)),
            Col(eventTsIso, rows, x => x.EventTs.ToString("O")),
            Col(localReceiveTsMs, rows, x => ToUnixMs(x.LocalReceiveTs)),
            Col(symbol, rows, x => x.Symbol),
            Const(exchange, rows.Count, Datasets.Exchange),
            Col(firstUpdateId, rows, x => x.FirstUpdateId),
            Col(lastUpdateId, rows, x => x.LastUpdateId),
            Col(bidUpdatesJson, rows, x => x.BidUpdatesJson),
            Col(askUpdatesJson, rows, x => x.AskUpdatesJson),
            Col(bidUpdateCount, rows, x => x.BidUpdateCount),
            Col(askUpdateCount, rows, x => x.AskUpdateCount),
            Col(receiveLatencyMs, rows, x => x.ReceiveLatencyMs),
            Col(rawPayloadJson, rows, x => x.RawPayloadJson)
        ], ct);
    }

    private async Task WriteRawAggTradesAsync(IReadOnlyList<RawAggTradeRow> rows, string filePath, CancellationToken ct)
    {
        var eventTsMs = new DataField<long>("event_ts_ms");
        var eventTsIso = new DataField<string>("event_ts_iso");
        var tradeTsMs = new DataField<long>("trade_ts_ms");
        var localReceiveTsMs = new DataField<long>("local_receive_ts_ms");
        var symbol = new DataField<string>("symbol");
        var exchange = new DataField<string>("exchange");
        var aggTradeId = new DataField<long>("agg_trade_id");
        var firstTradeId = new DataField<long>("first_trade_id");
        var lastTradeId = new DataField<long>("last_trade_id");
        var price = new DataField<double>("price");
        var quantity = new DataField<double>("quantity");
        var buyerIsMaker = new DataField<bool>("buyer_is_maker");
        var tradeSide = new DataField<string>("trade_side");
        var receiveLatencyMs = new DataField<double>("receive_latency_ms");
        var rawPayloadJson = new DataField<string>("raw_payload_json");

        var schema = new ParquetSchema(eventTsMs, eventTsIso, tradeTsMs, localReceiveTsMs, symbol, exchange,
            aggTradeId, firstTradeId, lastTradeId, price, quantity, buyerIsMaker, tradeSide, receiveLatencyMs, rawPayloadJson);

        await WriteColumnsAsync(filePath, schema,
        [
            Col(eventTsMs, rows, x => ToUnixMs(x.EventTs)),
            Col(eventTsIso, rows, x => x.EventTs.ToString("O")),
            Col(tradeTsMs, rows, x => ToUnixMs(x.TradeTs)),
            Col(localReceiveTsMs, rows, x => ToUnixMs(x.LocalReceiveTs)),
            Col(symbol, rows, x => x.Symbol),
            Const(exchange, rows.Count, Datasets.Exchange),
            Col(aggTradeId, rows, x => x.AggTradeId),
            Col(firstTradeId, rows, x => x.FirstTradeId),
            Col(lastTradeId, rows, x => x.LastTradeId),
            Col(price, rows, x => x.Price),
            Col(quantity, rows, x => x.Quantity),
            Col(buyerIsMaker, rows, x => x.BuyerIsMaker),
            Col(tradeSide, rows, x => x.TradeSide),
            Col(receiveLatencyMs, rows, x => x.ReceiveLatencyMs),
            Col(rawPayloadJson, rows, x => x.RawPayloadJson)
        ], ct);
    }

    private async Task WriteBookChangesAsync(IReadOnlyList<BookChangeRow> rows, string filePath, CancellationToken ct)
    {
        var eventTsMs = new DataField<long>("event_ts_ms");
        var eventTsIso = new DataField<string>("event_ts_iso");
        var localReceiveTsMs = new DataField<long>("local_receive_ts_ms");
        var symbol = new DataField<string>("symbol");
        var exchange = new DataField<string>("exchange");
        var eventType = new DataField<string>("event_type");
        var side = new DataField<string>("side");
        var price = new DataField<double>("price");
        var previousQuantity = new DataField<double>("previous_quantity");
        var newQuantity = new DataField<double>("new_quantity");
        var deltaQuantity = new DataField<double>("delta_quantity");
        var absoluteDeltaQuantity = new DataField<double>("absolute_delta_quantity");
        var isBestLevel = new DataField<bool>("is_best_level");
        var firstUpdateId = new DataField<long>("first_update_id");
        var lastUpdateId = new DataField<long>("last_update_id");
        var bookLastUpdateId = new DataField<long>("book_last_update_id");

        var schema = new ParquetSchema(eventTsMs, eventTsIso, localReceiveTsMs, symbol, exchange, eventType, side,
            price, previousQuantity, newQuantity, deltaQuantity, absoluteDeltaQuantity, isBestLevel, firstUpdateId,
            lastUpdateId, bookLastUpdateId);

        await WriteColumnsAsync(filePath, schema,
        [
            Col(eventTsMs, rows, x => ToUnixMs(x.EventTs)),
            Col(eventTsIso, rows, x => x.EventTs.ToString("O")),
            Col(localReceiveTsMs, rows, x => ToUnixMs(x.LocalReceiveTs)),
            Col(symbol, rows, x => x.Symbol),
            Const(exchange, rows.Count, Datasets.Exchange),
            Col(eventType, rows, x => x.EventType),
            Col(side, rows, x => x.Side),
            Col(price, rows, x => x.Price),
            Col(previousQuantity, rows, x => x.PreviousQuantity),
            Col(newQuantity, rows, x => x.NewQuantity),
            Col(deltaQuantity, rows, x => x.DeltaQuantity),
            Col(absoluteDeltaQuantity, rows, x => x.AbsoluteDeltaQuantity),
            Col(isBestLevel, rows, x => x.IsBestLevel),
            Col(firstUpdateId, rows, x => x.FirstUpdateId),
            Col(lastUpdateId, rows, x => x.LastUpdateId),
            Col(bookLastUpdateId, rows, x => x.BookLastUpdateId)
        ], ct);
    }

    private async Task WriteFeaturesAsync(IReadOnlyList<FeatureRow> rows, string filePath, CancellationToken ct)
    {
        var eventTsMs = new DataField<long>("event_ts_ms");
        var eventTsIso = new DataField<string>("event_ts_iso");
        var localComputeTsMs = new DataField<long>("local_compute_ts_ms");
        var symbol = new DataField<string>("symbol");
        var exchange = new DataField<string>("exchange");
        var featureIntervalMs = new DataField<int>("feature_interval_ms");
        var rollingWindowMs = new DataField<int>("rolling_window_ms");
        var bookLastUpdateId = new DataField<long>("book_last_update_id");
        var bestBid = new DataField<double>("best_bid");
        var bestAsk = new DataField<double>("best_ask");
        var midprice = new DataField<double>("midprice");
        var spread = new DataField<double>("spread");
        var microprice = new DataField<double>("microprice");
        var bestBidSize = new DataField<double>("best_bid_size");
        var bestAskSize = new DataField<double>("best_ask_size");
        var totalBidDepthL5 = new DataField<double>("total_bid_depth_l5");
        var totalAskDepthL5 = new DataField<double>("total_ask_depth_l5");
        var orderFlowImbalance = new DataField<double>("order_flow_imbalance");
        var buyTradeVolumeWindow = new DataField<double>("buy_trade_volume_window");
        var sellTradeVolumeWindow = new DataField<double>("sell_trade_volume_window");
        var tradeImbalance = new DataField<double>("trade_imbalance");
        var limitAddBidWindow = new DataField<double>("limit_add_bid_window");
        var limitAddAskWindow = new DataField<double>("limit_add_ask_window");
        var cancelBidWindow = new DataField<double>("cancel_bid_window");
        var cancelAskWindow = new DataField<double>("cancel_ask_window");

        var schema = new ParquetSchema(eventTsMs, eventTsIso, localComputeTsMs, symbol, exchange, featureIntervalMs,
            rollingWindowMs, bookLastUpdateId, bestBid, bestAsk, midprice, spread, microprice, bestBidSize, bestAskSize,
            totalBidDepthL5, totalAskDepthL5, orderFlowImbalance, buyTradeVolumeWindow, sellTradeVolumeWindow,
            tradeImbalance, limitAddBidWindow, limitAddAskWindow, cancelBidWindow, cancelAskWindow);

        await WriteColumnsAsync(filePath, schema,
        [
            Col(eventTsMs, rows, x => ToUnixMs(x.EventTs)),
            Col(eventTsIso, rows, x => x.EventTs.ToString("O")),
            Col(localComputeTsMs, rows, x => ToUnixMs(x.LocalComputeTs)),
            Col(symbol, rows, x => x.Symbol),
            Const(exchange, rows.Count, Datasets.Exchange),
            Col(featureIntervalMs, rows, x => x.FeatureIntervalMs),
            Col(rollingWindowMs, rows, x => x.RollingWindowMs),
            Col(bookLastUpdateId, rows, x => x.BookLastUpdateId),
            Col(bestBid, rows, x => x.BestBid),
            Col(bestAsk, rows, x => x.BestAsk),
            Col(midprice, rows, x => x.Midprice),
            Col(spread, rows, x => x.Spread),
            Col(microprice, rows, x => x.Microprice),
            Col(bestBidSize, rows, x => x.BestBidSize),
            Col(bestAskSize, rows, x => x.BestAskSize),
            Col(totalBidDepthL5, rows, x => x.TotalBidDepthL5),
            Col(totalAskDepthL5, rows, x => x.TotalAskDepthL5),
            Col(orderFlowImbalance, rows, x => x.OrderFlowImbalance),
            Col(buyTradeVolumeWindow, rows, x => x.BuyTradeVolumeWindow),
            Col(sellTradeVolumeWindow, rows, x => x.SellTradeVolumeWindow),
            Col(tradeImbalance, rows, x => x.TradeImbalance),
            Col(limitAddBidWindow, rows, x => x.LimitAddBidWindow),
            Col(limitAddAskWindow, rows, x => x.LimitAddAskWindow),
            Col(cancelBidWindow, rows, x => x.CancelBidWindow),
            Col(cancelAskWindow, rows, x => x.CancelAskWindow)
        ], ct);
    }

    private async Task WriteSnapshotsAsync(IReadOnlyList<SnapshotRow> rows, string filePath, CancellationToken ct)
    {
        var eventTsMs = new DataField<long>("event_ts_ms");
        var eventTsIso = new DataField<string>("event_ts_iso");
        var localReceiveTsMs = new DataField<long>("local_receive_ts_ms");
        var symbol = new DataField<string>("symbol");
        var exchange = new DataField<string>("exchange");
        var lastUpdateId = new DataField<long>("last_update_id");
        var bidsJson = new DataField<string>("bids_json");
        var asksJson = new DataField<string>("asks_json");
        var bidLevelCount = new DataField<int>("bid_level_count");
        var askLevelCount = new DataField<int>("ask_level_count");
        var depthSynchronized = new DataField<bool>("depth_synchronized");
        var resyncCount = new DataField<long>("resync_count");

        var schema = new ParquetSchema(eventTsMs, eventTsIso, localReceiveTsMs, symbol, exchange, lastUpdateId,
            bidsJson, asksJson, bidLevelCount, askLevelCount, depthSynchronized, resyncCount);

        await WriteColumnsAsync(filePath, schema,
        [
            Col(eventTsMs, rows, x => ToUnixMs(x.EventTs)),
            Col(eventTsIso, rows, x => x.EventTs.ToString("O")),
            Col(localReceiveTsMs, rows, x => ToUnixMs(x.LocalReceiveTs)),
            Col(symbol, rows, x => x.Symbol),
            Const(exchange, rows.Count, Datasets.Exchange),
            Col(lastUpdateId, rows, x => x.LastUpdateId),
            Col(bidsJson, rows, x => x.BidsJson),
            Col(asksJson, rows, x => x.AsksJson),
            Col(bidLevelCount, rows, x => x.BidLevelCount),
            Col(askLevelCount, rows, x => x.AskLevelCount),
            Col(depthSynchronized, rows, x => x.DepthSynchronized),
            Col(resyncCount, rows, x => x.ResyncCount)
        ], ct);
    }

    private static async Task WriteColumnsAsync(
        string filePath,
        ParquetSchema schema,
        IReadOnlyList<DataColumn> columns,
        CancellationToken ct)
    {
        await using var stream = File.Create(filePath);
        await using var writer = await ParquetWriter.CreateAsync(schema, stream, cancellationToken: ct);
        writer.CompressionMethod = CompressionMethod.Snappy;
        using var rowGroup = writer.CreateRowGroup();
        foreach (var column in columns)
            await rowGroup.WriteColumnAsync(column, ct);
    }

    private string NextPartPath(string dataset, DateTime hour)
    {
        var dir = Path.Combine(
            _rootPath,
            dataset,
            $"symbol={_options.SymbolUpper}",
            $"date_utc={hour:yyyy-MM-dd}",
            $"hour_utc={hour:HH}");
        Directory.CreateDirectory(dir);
        var stem = _options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase)
            ? "part-sample"
            : $"part-{DateTime.UtcNow:yyyyMMddTHHmmssffffff}";
        return Path.Combine(dir, $"{stem}.parquet");
    }

    private static DateTime HourBucket(DateTime timestamp)
    {
        var utc = timestamp.ToUniversalTime();
        return new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
    }

    private static DateTime GetTimestamp<T>(T row) => row switch
    {
        RawDepthRow x => x.EventTs,
        RawAggTradeRow x => x.EventTs,
        BookChangeRow x => x.EventTs,
        FeatureRow x => x.EventTs,
        SnapshotRow x => x.EventTs,
        _ => DateTime.UtcNow
    };

    private void WriteMetadata(string dataset, string parquetPath, int rows, DateTime hour)
    {
        var metadata = new
        {
            dataset,
            symbol = _options.SymbolUpper,
            exchange = Datasets.Exchange,
            rows,
            hour_utc = hour.ToString("O"),
            file = Path.GetFileName(parquetPath),
            schema_version = "event-level-v1"
        };
        File.WriteAllText(
            Path.ChangeExtension(parquetPath, ".meta.json"),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static DataColumn Col<T, TValue>(DataField<TValue> field, IReadOnlyList<T> rows, Func<T, TValue> selector) =>
        new(field, rows.Select(selector).ToArray());

    private static DataColumn Const<TValue>(DataField<TValue> field, int count, TValue value) =>
        new(field, Enumerable.Repeat(value, count).ToArray());

    private static long ToUnixMs(DateTime ts) =>
        new DateTimeOffset(DateTime.SpecifyKind(ts.ToUniversalTime(), DateTimeKind.Utc)).ToUnixTimeMilliseconds();
}
