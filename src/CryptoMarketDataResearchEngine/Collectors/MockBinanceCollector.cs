using System.Text.Json;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Diagnostics;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;
using CryptoMarketDataResearchEngine.Treatment;

namespace CryptoMarketDataResearchEngine.Collectors;

public sealed class MockBinanceCollector : IMarketDataCollector
{
    private readonly CaptureOptions _options;
    private readonly IMarketDataSink _sink;
    private readonly WebSocketLatencyTracker _latency;
    private readonly BookTreatmentEngine _book;

    public MockBinanceCollector(CaptureOptions options, IMarketDataSink sink, WebSocketLatencyTracker latency)
    {
        _options = options;
        _sink = sink;
        _latency = latency;
        _book = new BookTreatmentEngine(options);
    }

    public async Task<CaptureResult> RunAsync(CancellationToken ct = default)
    {
        var rng = new Random(42);
        var events = Math.Max(100, _options.CaptureDurationSeconds * _options.MockEventsPerSecond);
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        const double basePrice = 65_000;
        var lastUpdateId = 1_000_000L;
        double? previousMockBid = null;
        double? previousMockAsk = null;

        var bids = Enumerable.Range(1, 20)
            .Select(i => new Level(Math.Round(basePrice - 100 - i * 0.5, 2), 1.0 + i * 0.18))
            .ToArray();
        var asks = Enumerable.Range(1, 20)
            .Select(i => new Level(Math.Round(basePrice + 100 + i * 0.5, 2), 1.0 + i * 0.18))
            .ToArray();
        _sink.Enqueue(_book.ApplySnapshot(lastUpdateId, bids, asks, start));

        for (var i = 0; i < events; i++)
        {
            ct.ThrowIfCancellationRequested();
            var eventTs = start.AddMilliseconds(i * 10);
            var baseLatencyMs = 5.6 + Math.Sin(i / 23.0) * 0.9 + rng.NextDouble() * 1.8;
            if (i % 83 == 0) baseLatencyMs += 4.8;
            var depthReceiveTs = eventTs.AddMilliseconds(Math.Max(1.5, baseLatencyMs));
            var tradeReceiveTs = eventTs.AddMilliseconds(Math.Max(1.5, baseLatencyMs + (rng.NextDouble() - 0.5) * 1.6));
            var priceShift = Math.Sin(i / 18.0) * 18.0 + Math.Sin(i / 57.0) * 9.0;
            if (i > events * 0.44 && i < events * 0.54) priceShift += 24.0;
            if (i > events * 0.70) priceShift -= 14.0;

            var quoteMid = basePrice + priceShift;
            var halfSpread = 0.01 + Math.Abs(Math.Sin(i / 29.0)) * 0.04 + rng.NextDouble() * 0.01;
            var bidPrice = Math.Round(quoteMid - halfSpread, 2);
            var askPrice = Math.Round(quoteMid + halfSpread, 2);
            var bidSize = Math.Round(Math.Max(0.05, 1.0 + Math.Sin(i / 13.0) * 0.7 + rng.NextDouble() * 0.8), 6);
            var askSize = Math.Round(Math.Max(0.05, 1.0 + Math.Cos(i / 17.0) * 0.7 + rng.NextDouble() * 0.8), 6);
            var bidUpdates = new List<Level>();
            var askUpdates = new List<Level>();
            if (previousMockBid is { } oldBid && Math.Abs(oldBid - bidPrice) > double.Epsilon)
                bidUpdates.Add(new Level(oldBid, 0));
            if (previousMockAsk is { } oldAsk && Math.Abs(oldAsk - askPrice) > double.Epsilon)
                askUpdates.Add(new Level(oldAsk, 0));
            bidUpdates.Add(new Level(bidPrice, bidSize));
            askUpdates.Add(new Level(askPrice, askSize));
            previousMockBid = bidPrice;
            previousMockAsk = askPrice;
            var updateId = lastUpdateId + i + 1;

            var rawDepth = new RawDepthRow(
                EventTs: eventTs,
                LocalReceiveTs: depthReceiveTs,
                Symbol: _options.SymbolUpper,
                FirstUpdateId: updateId,
                LastUpdateId: updateId,
                BidUpdatesJson: JsonSerializer.Serialize(bidUpdates),
                AskUpdatesJson: JsonSerializer.Serialize(askUpdates),
                BidUpdateCount: bidUpdates.Count,
                AskUpdateCount: askUpdates.Count,
                ReceiveLatencyMs: _latency.Record("mock.depth", eventTs, depthReceiveTs),
                RawPayloadJson: _options.RawPayload ? JsonSerializer.Serialize(new { e = "depthUpdate", U = updateId, u = updateId }) : "");
            _sink.Enqueue(rawDepth);

            foreach (var row in _book.ApplyDepthUpdate(eventTs, depthReceiveTs, updateId, updateId, bidUpdates, askUpdates))
                _sink.Enqueue(row);

            var quantityBoost = i > events * 0.44 && i < events * 0.58 ? 2.2 : 1.0;
            var quantity = Math.Round((0.02 + rng.NextDouble() * 0.5) * quantityBoost, 6);
            var buyerIsMaker = i < events * 0.55 ? i % 5 == 0 : i % 3 != 0;
            var trade = new RawAggTradeRow(
                EventTs: eventTs,
                TradeTs: eventTs,
                LocalReceiveTs: tradeReceiveTs,
                Symbol: _options.SymbolUpper,
                AggTradeId: 900_000 + i,
                FirstTradeId: 800_000 + i,
                LastTradeId: 800_000 + i,
                Price: Math.Round(quoteMid + (rng.NextDouble() - 0.5) * halfSpread, 2),
                Quantity: quantity,
                BuyerIsMaker: buyerIsMaker,
                TradeSide: buyerIsMaker ? "sell" : "buy",
                ReceiveLatencyMs: _latency.Record("mock.aggTrade", eventTs, tradeReceiveTs),
                RawPayloadJson: _options.RawPayload ? JsonSerializer.Serialize(new { e = "aggTrade", a = 900_000 + i }) : "");
            _sink.Enqueue(trade);
            _book.RecordTrade(trade);

            if (_book.BuildFeature(eventTs) is { } feature)
                _sink.Enqueue(feature);

            await Task.Yield();
        }

        var rows = await _sink.FlushAsync(ct);
        return new CaptureResult(_options.Mode, _options.SymbolUpper, Path.GetFullPath(_options.OutputPath), rows, _latency.Summaries(), []);
    }
}
