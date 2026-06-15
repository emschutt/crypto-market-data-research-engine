using System.Globalization;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;

namespace CryptoMarketDataResearchEngine.Diagnostics;

public static class MarketDataQualityValidator
{
    public static IReadOnlyList<QualityCheck> Validate(
        CaptureOptions options,
        IReadOnlyDictionary<string, int> rows,
        MarketDataSample sample,
        bool strict)
    {
        var checks = new List<QualityCheck>();
        var expectedEvents = Math.Max(100, options.CaptureDurationSeconds * options.MockEventsPerSecond);

        CheckRowCount(Datasets.RawDepth, expectedEvents, strict, rows, checks);
        CheckRowCount(Datasets.RawAggTrades, expectedEvents, strict, rows, checks);
        CheckRowCount(Datasets.BookChangeEvents, expectedEvents, strict, rows, checks);
        CheckRowCount(Datasets.Features, expectedEvents, strict, rows, checks);
        Check("row count: snapshots", rows.GetValueOrDefault(Datasets.Snapshots) >= 1,
            $"{rows.GetValueOrDefault(Datasets.Snapshots)} rows, expected >= 1", checks);

        Check("timestamps: raw_depth monotonic", IsMonotonic(sample.Depth.Select(x => x.EventTs)),
            "depth event timestamps are non-decreasing", checks);
        Check("timestamps: raw_agg_trades monotonic", IsMonotonic(sample.Trades.Select(x => x.TradeTs)),
            "trade timestamps are non-decreasing", checks);
        Check("timestamps: features monotonic", IsMonotonic(sample.Features.Select(x => x.EventTs)),
            "feature timestamps are non-decreasing", checks);

        Check("symbols: all rows match requested symbol", AllSymbolsMatch(options.SymbolUpper, sample),
            $"all sampled rows use {options.SymbolUpper}", checks);

        Check("trades: prices positive", sample.Trades.All(x => x.Price > 0),
            Range(sample.Trades.Select(x => x.Price), "price"), checks);
        Check("trades: quantities positive", sample.Trades.All(x => x.Quantity > 0),
            Range(sample.Trades.Select(x => x.Quantity), "quantity"), checks);
        Check("trades: side matches maker flag", sample.Trades.All(x => x.TradeSide == (x.BuyerIsMaker ? "sell" : "buy")),
            "buyer_is_maker=true maps to sell, false maps to buy", checks);
        Check("trades: ids ordered", sample.Trades.All(x => x.FirstTradeId <= x.LastTradeId),
            "first_trade_id <= last_trade_id", checks);

        Check("depth: update ids ordered", sample.Depth.All(x => x.FirstUpdateId <= x.LastUpdateId),
            "first_update_id <= last_update_id", checks);
        Check("depth: update counts non-negative", sample.Depth.All(x => x.BidUpdateCount >= 0 && x.AskUpdateCount >= 0),
            "bid_update_count and ask_update_count are non-negative", checks);

        var latencies = sample.Trades.Select(x => x.ReceiveLatencyMs).Concat(sample.Depth.Select(x => x.ReceiveLatencyMs)).ToArray();
        Check("latency: non-negative", latencies.All(x => x >= 0),
            Range(latencies, "receive_latency_ms"), checks);
        Check("latency: mock bounded", !strict || latencies.All(x => x is >= 0 and <= 25),
            strict ? Range(latencies, "receive_latency_ms") : $"not enforced for {options.Mode} mode", checks);

        Check("book changes: quantities non-negative after update", sample.Changes.All(x => x.PreviousQuantity >= 0 && x.NewQuantity >= 0),
            "previous_quantity and new_quantity are non-negative", checks);
        Check("book changes: absolute delta matches signed delta", sample.Changes.All(x => NearlyEqual(x.AbsoluteDeltaQuantity, Math.Abs(x.DeltaQuantity))),
            "absolute_delta_quantity == abs(delta_quantity)", checks);
        Check("book changes: event type valid", sample.Changes.All(x => x.EventType is "limit_add" or "cancel_or_trade"),
            "event_type is limit_add or cancel_or_trade", checks);
        Check("book changes: side valid", sample.Changes.All(x => x.Side is "bid" or "ask"),
            "side is bid or ask", checks);

        Check("features: best bid/ask positive", sample.Features.All(x => x.BestBid > 0 && x.BestAsk > 0),
            $"best bid/ask positive across {sample.Features.Count} samples", checks);
        Check("features: best bid below/equal ask", sample.Features.All(x => x.BestBid <= x.BestAsk),
            "best_bid <= best_ask", checks);
        Check("features: spread consistent", sample.Features.All(x => NearlyEqual(x.Spread, Math.Max(x.BestAsk - x.BestBid, 0))),
            "spread == max(best_ask - best_bid, 0)", checks);
        Check("features: midprice consistent", sample.Features.All(x => NearlyEqual(x.Midprice, (x.BestBid + x.BestAsk) / 2.0)),
            "midprice == (best_bid + best_ask) / 2", checks);
        Check("features: microprice inside quote", sample.Features.All(x => x.Microprice >= x.BestBid && x.Microprice <= x.BestAsk),
            "microprice is between best_bid and best_ask", checks);
        Check("features: L5 depths non-negative", sample.Features.All(x => x.TotalBidDepthL5 >= 0 && x.TotalAskDepthL5 >= 0),
            "total_bid_depth_l5 and total_ask_depth_l5 are non-negative", checks);
        Check("features: top-of-book sizes match L1", sample.Features.All(x => NearlyEqual(x.BestBidSize, x.BidL1Size) && NearlyEqual(x.BestAskSize, x.AskL1Size)),
            "best bid/ask sizes match L1 size columns", checks);
        Check("features: L5 bid ladder sorted descending", sample.Features.All(BidsSortedDescending),
            "bid_l1_price >= ... >= bid_l5_price for populated levels", checks);
        Check("features: L5 ask ladder sorted ascending", sample.Features.All(AsksSortedAscending),
            "ask_l1_price <= ... <= ask_l5_price for populated levels", checks);
        Check("features: imbalance ranges", sample.Features.All(x =>
                IsBetween(x.OrderFlowImbalance, -1, 1) && IsBetween(x.TradeImbalance, -1, 1)),
            "order_flow_imbalance and trade_imbalance are between -1 and 1", checks);
        Check("features: rolling quantities non-negative", sample.Features.All(x =>
                x.BuyTradeVolumeWindow >= 0 && x.SellTradeVolumeWindow >= 0 &&
                x.LimitAddBidWindow >= 0 && x.LimitAddAskWindow >= 0 &&
                x.CancelBidWindow >= 0 && x.CancelAskWindow >= 0),
            "rolling trade, add, and cancel quantities are non-negative", checks);

        Check("snapshots: synchronized", sample.Snapshots.All(x => x.DepthSynchronized),
            "snapshot rows mark depth_synchronized=true", checks);
        Check("snapshots: levels present", sample.Snapshots.All(x => x.BidLevelCount > 0 && x.AskLevelCount > 0),
            "snapshot bid and ask levels are present", checks);

        return checks;
    }

    public static void ThrowIfFailed(IReadOnlyList<QualityCheck> checks)
    {
        var failures = checks.Where(x => !x.Passed).ToArray();
        if (failures.Length == 0) return;

        throw new InvalidOperationException(
            "Market data quality validation failed:" + Environment.NewLine +
            string.Join(Environment.NewLine, failures.Select(x => $"- {x.Name}: {x.Detail}")));
    }

    private static void Check(string name, bool passed, string detail, ICollection<QualityCheck> checks) =>
        checks.Add(new QualityCheck(name, passed, detail));

    private static void CheckRowCount(
        string dataset,
        int expectedEvents,
        bool strict,
        IReadOnlyDictionary<string, int> rows,
        ICollection<QualityCheck> checks)
    {
        var actual = rows.GetValueOrDefault(dataset);
        Check(
            $"row count: {dataset}",
            !strict || actual >= expectedEvents,
            strict ? $"{actual} rows, expected >= {expectedEvents}" : $"{actual} rows captured; minimum enforced only in mock smoke mode",
            checks);
    }

    private static bool IsMonotonic(IEnumerable<DateTime> values)
    {
        DateTime? previous = null;
        foreach (var value in values)
        {
            if (previous is not null && value < previous.Value)
                return false;
            previous = value;
        }

        return true;
    }

    private static bool AllSymbolsMatch(string symbol, MarketDataSample sample) =>
        sample.Depth.All(x => x.Symbol == symbol) &&
        sample.Trades.All(x => x.Symbol == symbol) &&
        sample.Changes.All(x => x.Symbol == symbol) &&
        sample.Features.All(x => x.Symbol == symbol) &&
        sample.Snapshots.All(x => x.Symbol == symbol);

    private static string Range(IEnumerable<double> values, string name)
    {
        var array = values.ToArray();
        if (array.Length == 0) return $"{name}: no sampled values";
        return string.Create(CultureInfo.InvariantCulture,
            $"{name}: min={array.Min():0.######}, max={array.Max():0.######}, avg={array.Average():0.######}");
    }

    private static bool BidsSortedDescending(FeatureRow row) =>
        Sorted([row.BidL1Price, row.BidL2Price, row.BidL3Price, row.BidL4Price, row.BidL5Price], descending: true);

    private static bool AsksSortedAscending(FeatureRow row) =>
        Sorted([row.AskL1Price, row.AskL2Price, row.AskL3Price, row.AskL4Price, row.AskL5Price], descending: false);

    private static bool Sorted(IReadOnlyList<double> values, bool descending)
    {
        var populated = values.Where(x => x > 0).ToArray();
        for (var i = 1; i < populated.Length; i++)
        {
            if (descending && populated[i - 1] < populated[i]) return false;
            if (!descending && populated[i - 1] > populated[i]) return false;
        }

        return true;
    }

    private static bool IsBetween(double value, double min, double max) =>
        value >= min && value <= max;

    private static bool NearlyEqual(double left, double right) =>
        Math.Abs(left - right) <= 0.000001;
}

public sealed record QualityCheck(string Name, bool Passed, string Detail);
