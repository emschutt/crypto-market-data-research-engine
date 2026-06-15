using System.Text.Json;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Models;

namespace CryptoMarketDataResearchEngine.Treatment;

public sealed class BookTreatmentEngine
{
    private readonly CaptureOptions _options;
    private readonly SortedDictionary<double, double> _bids = new();
    private readonly SortedDictionary<double, double> _asks = new();
    private readonly Queue<TradeSample> _trades = new();
    private readonly Queue<ChangeSample> _changes = new();
    private long _bookLastUpdateId;
    private long _lastFeatureTsMs;

    public BookTreatmentEngine(CaptureOptions options)
    {
        _options = options;
    }

    public SnapshotRow ApplySnapshot(long lastUpdateId, IReadOnlyList<Level> bids, IReadOnlyList<Level> asks, DateTime timestamp)
    {
        _bids.Clear();
        _asks.Clear();
        foreach (var level in bids.Where(x => x.Size > 0))
            _bids[level.Price] = level.Size;
        foreach (var level in asks.Where(x => x.Size > 0))
            _asks[level.Price] = level.Size;

        _bookLastUpdateId = lastUpdateId;

        return new SnapshotRow(
            EventTs: timestamp,
            LocalReceiveTs: timestamp,
            Symbol: _options.SymbolUpper,
            LastUpdateId: lastUpdateId,
            BidsJson: JsonSerializer.Serialize(bids),
            AsksJson: JsonSerializer.Serialize(asks),
            BidLevelCount: bids.Count,
            AskLevelCount: asks.Count,
            DepthSynchronized: true,
            ResyncCount: 0);
    }

    public IReadOnlyList<BookChangeRow> ApplyDepthUpdate(
        DateTime eventTs,
        DateTime localReceiveTs,
        long firstUpdateId,
        long lastUpdateId,
        IReadOnlyList<Level> bidUpdates,
        IReadOnlyList<Level> askUpdates)
    {
        var rows = new List<BookChangeRow>(bidUpdates.Count + askUpdates.Count);
        ApplySide("bid", _bids, bidUpdates, eventTs, localReceiveTs, firstUpdateId, lastUpdateId, rows);
        ApplySide("ask", _asks, askUpdates, eventTs, localReceiveTs, firstUpdateId, lastUpdateId, rows);
        _bookLastUpdateId = lastUpdateId;
        TrimWindows(ToUnixMs(eventTs));
        return rows;
    }

    public void RecordTrade(RawAggTradeRow trade)
    {
        var tradeTsMs = ToUnixMs(trade.TradeTs);
        _trades.Enqueue(new TradeSample(tradeTsMs, trade.TradeSide, trade.Quantity));
        TrimWindows(tradeTsMs);
    }

    public FeatureRow? BuildFeature(DateTime eventTs)
    {
        if (_bids.Count == 0 || _asks.Count == 0)
            return null;

        var eventTsMs = ToUnixMs(eventTs);
        if (_options.FeatureIntervalMs > 0 &&
            _lastFeatureTsMs > 0 &&
            eventTsMs - _lastFeatureTsMs < _options.FeatureIntervalMs)
        {
            return null;
        }

        _lastFeatureTsMs = eventTsMs;
        TrimWindows(eventTsMs);

        var topBids = _bids.Reverse().Take(5).Select(x => new Level(x.Key, x.Value)).ToArray();
        var topAsks = _asks.Take(5).Select(x => new Level(x.Key, x.Value)).ToArray();
        if (topBids.Length == 0 || topAsks.Length == 0)
            return null;

        var bestBid = topBids[0];
        var bestAsk = topAsks[0];
        var mid = (bestBid.Price + bestAsk.Price) / 2.0;
        var queueTotal = bestBid.Size + bestAsk.Size;
        var microprice = queueTotal > 0
            ? (bestBid.Price * bestAsk.Size + bestAsk.Price * bestBid.Size) / queueTotal
            : mid;

        var totalBidDepth = topBids.Sum(x => x.Size);
        var totalAskDepth = topAsks.Sum(x => x.Size);
        var depthTotal = totalBidDepth + totalAskDepth;
        var buyVolume = _trades.Where(x => x.Side == "buy").Sum(x => x.Quantity);
        var sellVolume = _trades.Where(x => x.Side == "sell").Sum(x => x.Quantity);
        var tradeTotal = buyVolume + sellVolume;

        return new FeatureRow(
            EventTs: eventTs,
            LocalComputeTs: _options.Mode.Equals("mock", StringComparison.OrdinalIgnoreCase) ? eventTs : DateTime.UtcNow,
            Symbol: _options.SymbolUpper,
            FeatureIntervalMs: _options.FeatureIntervalMs,
            RollingWindowMs: _options.RollingWindowMs,
            BookLastUpdateId: _bookLastUpdateId,
            BestBid: bestBid.Price,
            BestAsk: bestAsk.Price,
            Midprice: mid,
            Spread: Math.Max(bestAsk.Price - bestBid.Price, 0),
            Microprice: microprice,
            BestBidSize: bestBid.Size,
            BestAskSize: bestAsk.Size,
            TotalBidDepthL5: totalBidDepth,
            TotalAskDepthL5: totalAskDepth,
            BidL1Price: ValueAt(topBids, 0).Price,
            BidL2Price: ValueAt(topBids, 1).Price,
            BidL3Price: ValueAt(topBids, 2).Price,
            BidL4Price: ValueAt(topBids, 3).Price,
            BidL5Price: ValueAt(topBids, 4).Price,
            BidL1Size: ValueAt(topBids, 0).Size,
            BidL2Size: ValueAt(topBids, 1).Size,
            BidL3Size: ValueAt(topBids, 2).Size,
            BidL4Size: ValueAt(topBids, 3).Size,
            BidL5Size: ValueAt(topBids, 4).Size,
            AskL1Price: ValueAt(topAsks, 0).Price,
            AskL2Price: ValueAt(topAsks, 1).Price,
            AskL3Price: ValueAt(topAsks, 2).Price,
            AskL4Price: ValueAt(topAsks, 3).Price,
            AskL5Price: ValueAt(topAsks, 4).Price,
            AskL1Size: ValueAt(topAsks, 0).Size,
            AskL2Size: ValueAt(topAsks, 1).Size,
            AskL3Size: ValueAt(topAsks, 2).Size,
            AskL4Size: ValueAt(topAsks, 3).Size,
            AskL5Size: ValueAt(topAsks, 4).Size,
            OrderFlowImbalance: depthTotal > 0 ? (totalBidDepth - totalAskDepth) / depthTotal : 0,
            BuyTradeVolumeWindow: buyVolume,
            SellTradeVolumeWindow: sellVolume,
            TradeImbalance: tradeTotal > 0 ? (buyVolume - sellVolume) / tradeTotal : 0,
            LimitAddBidWindow: _changes.Where(x => x.Side == "bid" && x.EventType == "limit_add").Sum(x => x.AbsoluteDelta),
            LimitAddAskWindow: _changes.Where(x => x.Side == "ask" && x.EventType == "limit_add").Sum(x => x.AbsoluteDelta),
            CancelBidWindow: _changes.Where(x => x.Side == "bid" && x.EventType != "limit_add").Sum(x => x.AbsoluteDelta),
            CancelAskWindow: _changes.Where(x => x.Side == "ask" && x.EventType != "limit_add").Sum(x => x.AbsoluteDelta));
    }

    private void ApplySide(
        string side,
        SortedDictionary<double, double> book,
        IReadOnlyList<Level> updates,
        DateTime eventTs,
        DateTime localReceiveTs,
        long firstUpdateId,
        long lastUpdateId,
        List<BookChangeRow> rows)
    {
        var bestBefore = side == "bid"
            ? (_bids.Count == 0 ? 0 : _bids.Keys.Max())
            : (_asks.Count == 0 ? 0 : _asks.Keys.Min());

        foreach (var update in updates)
        {
            book.TryGetValue(update.Price, out var previous);
            if (update.Size <= 0)
                book.Remove(update.Price);
            else
                book[update.Price] = update.Size;

            var delta = update.Size - previous;
            if (Math.Abs(delta) < double.Epsilon)
                continue;

            var eventType = delta > 0 ? "limit_add" : "cancel_or_trade";
            var row = new BookChangeRow(
                EventTs: eventTs,
                LocalReceiveTs: localReceiveTs,
                Symbol: _options.SymbolUpper,
                EventType: eventType,
                Side: side,
                Price: update.Price,
                PreviousQuantity: previous,
                NewQuantity: update.Size,
                DeltaQuantity: delta,
                AbsoluteDeltaQuantity: Math.Abs(delta),
                IsBestLevel: Math.Abs(update.Price - bestBefore) < double.Epsilon,
                FirstUpdateId: firstUpdateId,
                LastUpdateId: lastUpdateId,
                BookLastUpdateId: lastUpdateId);

            rows.Add(row);
            _changes.Enqueue(new ChangeSample(ToUnixMs(eventTs), side, eventType, Math.Abs(delta)));
        }
    }

    private void TrimWindows(long nowMs)
    {
        var cutoff = nowMs - _options.RollingWindowMs;
        while (_trades.Count > 0 && _trades.Peek().TsMs < cutoff)
            _trades.Dequeue();
        while (_changes.Count > 0 && _changes.Peek().TsMs < cutoff)
            _changes.Dequeue();
    }

    private static long ToUnixMs(DateTime ts) =>
        new DateTimeOffset(DateTime.SpecifyKind(ts.ToUniversalTime(), DateTimeKind.Utc)).ToUnixTimeMilliseconds();

    private static Level ValueAt(IReadOnlyList<Level> levels, int index) =>
        index < levels.Count ? levels[index] : new Level(0, 0);

    private sealed record TradeSample(long TsMs, string Side, double Quantity);
    private sealed record ChangeSample(long TsMs, string Side, string EventType, double AbsoluteDelta);
}

public sealed record Level(double Price, double Size);
