using System.Globalization;
using System.Text;
using System.Linq;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Diagnostics;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;

namespace CryptoMarketDataResearchEngine.Export;

public static class DashboardSvgRenderer
{
    private static DateTime? _globalMinTs;
    private static DateTime? _globalMaxTs;
    private const int Width = 2200;
    private const int ContentX = 150;
    private const int ContentWidth = 1900;
    private const int PanelHeight = 350;
    private const int PanelGap = 42;

    public static string Render(
        CaptureOptions options,
        IReadOnlyDictionary<string, int> rows,
        MarketDataSample sample,
        IReadOnlyList<LatencySummary> latency,
        string outputPath)
    {
        Directory.CreateDirectory(outputPath);
        var path = Path.Combine(outputPath, "market-data-pipeline-dashboard.svg");

        // keep an all-rows copy for counts/audit, but downsample visualizations to 500 points
        var tradesAll = sample.Trades.OrderBy(x => x.TradeTs).ToArray();
        var depthAll = sample.Depth.OrderBy(x => x.EventTs).ToArray();
        var featuresAll = sample.Features.OrderBy(x => x.EventTs).ToArray();
        var changesAll = sample.Changes.OrderBy(x => x.EventTs).ToArray();

        var trades = Downsample<RawAggTradeRow>(tradesAll, x => x.TradeTs, 500);
        var depth = Downsample<RawDepthRow>(depthAll, x => x.EventTs, 500);
        var features = Downsample<FeatureRow>(featuresAll, x => x.EventTs, 500);
        var changes = Downsample<BookChangeRow>(changesAll, x => x.EventTs, 500);

        // compute a global time range across the plotted series so all charts share the same UTC ticks
        DateTime? globalMin = null;
        DateTime? globalMax = null;
        var times = new List<DateTime>();
        if (trades.Count > 0) { times.Add(trades.First().TradeTs); times.Add(trades.Last().TradeTs); }
        if (features.Count > 0) { times.Add(features.First().EventTs); times.Add(features.Last().EventTs); }
        if (depth.Count > 0) { times.Add(depth.First().EventTs); times.Add(depth.Last().EventTs); }
        if (changes.Count > 0) { times.Add(changes.First().EventTs); times.Add(changes.Last().EventTs); }
        if (times.Count > 0) { globalMin = times.Min(); globalMax = times.Max(); }
        _globalMinTs = globalMin;
        _globalMaxTs = globalMax;

        // Simplified 2-column layout: OHLC full width, then two columns for the remaining panels
        var panels = new StringBuilder();
        var y = 500;

        // OHLC - full width
        AppendPanel(panels, y, "OHLC Candles From Aggregate Trades", "Open, high, low, close candles built from event-level aggTrade prices.",
            RenderOhlc(ChartFrame(y), trades));
        y += PanelHeight + PanelGap;

        // two-column row 1: Midprice (left), Volume (right)
        var hGap = 36;
        var halfW = (ContentWidth - hGap) / 2;
        AppendPanelAt(panels, ContentX, y, (int)halfW, "Midprice And L1 Quote", "Best bid, best ask, and midprice from the treated L5 book.",
            RenderLineChart(ChartFrameAt(y, ContentX, halfW), features, x => x.EventTs, "USDT", "0.00",
                new LineSeries<FeatureRow>("midprice", "#f8fafc", x => x.Midprice),
                new LineSeries<FeatureRow>("best bid", "#53f3c3", x => x.BestBid),
                new LineSeries<FeatureRow>("best ask", "#ff6b8a", x => x.BestAsk)));
        AppendPanelAt(panels, ContentX + (int)halfW + hGap, y, (int)halfW, "Aggregate Trade Volume", "Stacked buy and sell volume per time bucket.",
            RenderVolume(ChartFrameAt(y, ContentX + halfW + hGap, halfW), trades));
        y += PanelHeight + PanelGap;

        // two-column row 2: Spread (left), Order-Flow Imbalance (right)
        AppendPanelAt(panels, ContentX, y, (int)halfW, "Bid-Ask Spread", "Quoted spread from the treated book.",
            RenderLineChart(ChartFrameAt(y, ContentX, halfW), features, x => x.EventTs, "USDT", "0.###",
                new LineSeries<FeatureRow>("spread", "#ffd166", x => x.Spread)));
        AppendPanelAt(panels, ContentX + (int)halfW + hGap, y, (int)halfW, "Order-Flow Imbalance", "Book imbalance and trade imbalance on the configured rolling window.",
            RenderLineChart(ChartFrameAt(y, ContentX + halfW + hGap, halfW), features, x => x.EventTs, "ratio", "0.###",
                new LineSeries<FeatureRow>("book OFI", "#7cc7ff", x => x.OrderFlowImbalance),
                new LineSeries<FeatureRow>("trade imbalance", "#ff9f43", x => x.TradeImbalance)));
        y += PanelHeight + PanelGap;

        // Dataset health - full width bottom
        AppendPanel(panels, y, "Dataset Health", "Rows written, sample timestamps, missing-data checks, and latency summary for the latest run.",
            RenderDatasetHealth(ChartFrame(y), rows, latency, sample));
        y += PanelHeight + 86;

        // Use counts from the full (undownsampled) arrays for accurate auditing
        var totalVolume = tradesAll.Sum(x => x.Quantity);
        var avgSpread = features.Count == 0 ? 0 : features.Select(x => x.Spread).Average();
        var avgLatency = tradesAll.Select(x => x.ReceiveLatencyMs).Concat(depthAll.Select(x => x.ReceiveLatencyMs)).DefaultIfEmpty(0).Average();
        var chartCount = featuresAll.Length;

        var svg = $$"""
<svg xmlns="http://www.w3.org/2000/svg" width="{{Width}}" height="{{y}}" viewBox="0 0 {{Width}} {{y}}">
  <defs>
    <linearGradient id="bg" x1="0" x2="1" y1="0" y2="1">
      <stop offset="0%" stop-color="#07111e"/>
      <stop offset="55%" stop-color="#10233b"/>
      <stop offset="100%" stop-color="#091424"/>
    </linearGradient>
    <radialGradient id="glow" cx="0.5" cy="0.02" r="0.55">
      <stop offset="0%" stop-color="#4ddcff" stop-opacity="0.18"/>
      <stop offset="100%" stop-color="#4ddcff" stop-opacity="0"/>
    </radialGradient>
    <filter id="shadow" x="-15%" y="-15%" width="130%" height="130%">
      <feDropShadow dx="0" dy="18" stdDeviation="20" flood-color="#000000" flood-opacity="0.32"/>
    </filter>
    <style>
      text { font-family: 'Aptos', 'Segoe UI', sans-serif; fill: #f8fafc; }
      .title { font-size: 70px; font-weight: 900; letter-spacing: -1px; }
      .subtitle { fill: #adbbcf; font-size: 28px; }
      .eyebrow { fill: #6ee7ff; font-size: 20px; font-weight: 900; letter-spacing: 5px; }
      .metric { font-size: 44px; font-weight: 900; }
      .label { fill: #8393aa; font-size: 17px; font-weight: 900; letter-spacing: 1.4px; }
      .panel { fill: #101d31; stroke: #2c425e; stroke-width: 2; }
      .panel-title { font-size: 28px; font-weight: 900; }
      .panel-subtitle { fill: #98a8bd; font-size: 18px; }
      .axis { stroke: #38506b; stroke-width: 2; }
      .grid { stroke: #22384f; stroke-width: 1; opacity: 0.8; }
      .tick { fill: #93a4b8; font-size: 16px; }
      .legend { fill: #cdd6e2; font-size: 18px; }
      .tiny { fill: #91a5be; font-size: 16px; }
    </style>
  </defs>
  <rect width="{{Width}}" height="{{y}}" fill="url(#bg)"/>
  <rect width="{{Width}}" height="{{y}}" fill="url(#glow)"/>

  <text x="{{Width / 2}}" y="90" text-anchor="middle" class="eyebrow">EVENT-LEVEL MARKET DATA RESEARCH ENGINE</text>
    <text x="{{Width / 2}}" y="176" text-anchor="middle" class="title">BTCUSDT Market Data Dashboard</text>
  <text x="{{Width / 2}}" y="224" text-anchor="middle" class="subtitle">High-frequency aggregate trades, L5 depth features, order-flow imbalance, OHLC candles, and latency diagnostics</text>

    {{MetricCard(ContentX, 290, 340, "Depth Updates", Count(rows, Datasets.RawDepth).ToString(CultureInfo.InvariantCulture), "#7cc7ff")}}
    {{MetricCard(ContentX + 390, 290, 340, "Feature Rows", chartCount.ToString(CultureInfo.InvariantCulture), "#ffd166")}}
    {{MetricCard(ContentX + 780, 290, 340, "Avg Spread", $"{N(avgSpread, "0.###")} USDT", "#ffcf5c")}}
    {{MetricCard(ContentX + 1170, 290, 340, "Avg Latency", $"{N(avgLatency, "0.###")} ms", "#c4a7ff")}}

  {{panels}}
</svg>
""";

        File.WriteAllText(path, svg);
        return path;
    }

    private static void AppendPanel(StringBuilder sb, int y, string title, string subtitle, string chart)
    {
        sb.AppendLine($$"""
  <rect x="{{ContentX}}" y="{{y}}" width="{{ContentWidth}}" height="{{PanelHeight}}" rx="26" class="panel" filter="url(#shadow)"/>
  <text x="{{ContentX + 36}}" y="{{y + 46}}" class="panel-title">{{Escape(title)}}</text>
  <text x="{{ContentX + 36}}" y="{{y + 76}}" class="panel-subtitle">{{Escape(subtitle)}}</text>
  {{chart}}
""");
    }

    private static void AppendPanelAt(StringBuilder sb, int x, int y, int width, string title, string subtitle, string chart)
    {
        sb.AppendLine($$"""
  <rect x="{{x}}" y="{{y}}" width="{{width}}" height="{{PanelHeight}}" rx="26" class="panel" filter="url(#shadow)"/>
  <text x="{{x + 36}}" y="{{y + 46}}" class="panel-title">{{Escape(title)}}</text>
  <text x="{{x + 36}}" y="{{y + 76}}" class="panel-subtitle">{{Escape(subtitle)}}</text>
  {{chart}}
""");
    }

    private static string RenderOhlc(Frame frame, IReadOnlyList<RawAggTradeRow> trades)
    {
        if (trades.Count == 0) return Empty(frame, "No aggregate trade rows were captured.");

        var candles = BuildCandles(trades, 28);
        if (candles.Count == 0) return Empty(frame, "Not enough trades to build OHLC candles.");

        var min = candles.Min(x => x.Low);
        var max = candles.Max(x => x.High);
        ExpandRange(ref min, ref max);
        var sb = new StringBuilder(Grid(frame));
        sb.Append(AxisLabels(frame, min, max, "0.00"));

        var slot = frame.W / candles.Count;
        var bodyWidth = Math.Clamp(slot * 0.5, 8, 34);
        for (var i = 0; i < candles.Count; i++)
        {
            var candle = candles[i];
            var x = frame.X + slot * (i + 0.5);
            var openY = Y(candle.Open, min, max, frame);
            var closeY = Y(candle.Close, min, max, frame);
            var highY = Y(candle.High, min, max, frame);
            var lowY = Y(candle.Low, min, max, frame);
            var color = candle.Close >= candle.Open ? "#53f3c3" : "#ff6b8a";
            var top = Math.Min(openY, closeY);
            var height = Math.Max(3, Math.Abs(closeY - openY));
            sb.AppendLine($$"""<line x1="{{F(x)}}" y1="{{F(highY)}}" x2="{{F(x)}}" y2="{{F(lowY)}}" stroke="{{color}}" stroke-width="3"/>""");
            sb.AppendLine($$"""<rect x="{{F(x - bodyWidth / 2)}}" y="{{F(top)}}" width="{{F(bodyWidth)}}" height="{{F(height)}}" rx="3" fill="{{color}}" opacity="0.88"/>""");
        }

        sb.Append(Legend(frame, ("up candle", "#53f3c3"), ("down candle", "#ff6b8a")));
        // add time ticks along the x-axis using the global time range when available
        if (trades.Count > 0)
        {
            var minTs = trades.First().TradeTs;
            var maxTs = trades.Last().TradeTs;
            if (_globalMinTs.HasValue) minTs = _globalMinTs.Value;
            if (_globalMaxTs.HasValue) maxTs = _globalMaxTs.Value;
            sb.Append(AxisTimeLabels(frame, minTs, maxTs));
        }
        return sb.ToString();
    }

    private static string RenderVolume(Frame frame, IReadOnlyList<RawAggTradeRow> trades)
    {
        if (trades.Count == 0) return Empty(frame, "No aggregate trade rows were captured.");

        var buckets = BuildTradeBuckets(trades, 34);
        var max = buckets.Select(x => x.Buy + x.Sell).DefaultIfEmpty(1).Max();
        ExpandRangeFromZero(ref max);
        var sb = new StringBuilder(Grid(frame));
        sb.Append(AxisLabels(frame, 0, max, "0.###"));

        var slot = frame.W / buckets.Count;
        var barWidth = Math.Clamp(slot * 0.58, 8, 32);
        for (var i = 0; i < buckets.Count; i++)
        {
            var x = frame.X + slot * i + slot / 2 - barWidth / 2;
            var buyHeight = frame.H - (Y(buckets[i].Buy, 0, max, frame) - frame.Y);
            var sellHeight = frame.H - (Y(buckets[i].Sell, 0, max, frame) - frame.Y);
            var buyY = frame.Y + frame.H - buyHeight;
            var sellY = buyY - sellHeight;
            sb.AppendLine($$"""<rect x="{{F(x)}}" y="{{F(buyY)}}" width="{{F(barWidth)}}" height="{{F(Math.Max(1, buyHeight))}}" rx="3" fill="#39d353" opacity="0.9"/>""");
            sb.AppendLine($$"""<rect x="{{F(x)}}" y="{{F(sellY)}}" width="{{F(barWidth)}}" height="{{F(Math.Max(1, sellHeight))}}" rx="3" fill="#ff5f6d" opacity="0.9"/>""");
        }

        sb.Append(Legend(frame, ("buy volume", "#39d353"), ("sell volume", "#ff5f6d")));
        if (buckets.Count > 0)
        {
            var minTs = buckets.First().Ts;
            var maxTs = buckets.Last().Ts;
            if (_globalMinTs.HasValue) minTs = _globalMinTs.Value;
            if (_globalMaxTs.HasValue) maxTs = _globalMaxTs.Value;
            sb.Append(AxisTimeLabels(frame, minTs, maxTs));
        }
        return sb.ToString();
    }

    private static string RenderBookChangeMix(Frame frame, IReadOnlyList<BookChangeRow> changes)
    {
        if (changes.Count == 0) return Empty(frame, "No book change rows were captured.");

        var buckets = BuildChangeBuckets(changes, 34);
        return RenderLineChart(frame, buckets, x => x.Ts, "events", "0.###",
            new LineSeries<ChangeBucket>("limit_add", "#5eead4", x => x.LimitAdds),
            new LineSeries<ChangeBucket>("cancel_or_trade", "#ff6b6b", x => x.Cancels));
    }

    private static string RenderLatency(Frame frame, IReadOnlyList<RawAggTradeRow> trades, IReadOnlyList<RawDepthRow> depth)
    {
        var tradeLatency = trades.Select(x => new LatencyPoint(x.TradeTs, x.ReceiveLatencyMs, "aggTrade")).ToArray();
        var depthLatency = depth.Select(x => new LatencyPoint(x.EventTs, x.ReceiveLatencyMs, "depth")).ToArray();
        var combined = tradeLatency.Concat(depthLatency).OrderBy(x => x.Ts).ToArray();
        if (combined.Length == 0) return Empty(frame, "No latency samples were captured.");

        return RenderLineChart(frame, combined, x => x.Ts, "ms", "0.###",
            new LineSeries<LatencyPoint>("aggTrade latency", "#53f3c3", x => x.Source == "aggTrade" ? x.Value : double.NaN),
            new LineSeries<LatencyPoint>("depth latency", "#91b4ff", x => x.Source == "depth" ? x.Value : double.NaN));
    }

    private static string RenderDatasetHealth(Frame frame, IReadOnlyDictionary<string, int> rows, IReadOnlyList<LatencySummary> latency, MarketDataSample sample)
    {
        var sb = new StringBuilder();
        var x = frame.X;
        var y = frame.Y + 6;
        var rowH = 34;
        sb.AppendLine($$"""<rect x="{{F(x)}}" y="{{F(y)}}" width="{{F(frame.W)}}" height="{{F(frame.H - 12)}}" rx="18" fill="#0b1627" stroke="#263c56" stroke-width="2"/>""");
        sb.AppendLine($$"""<text x="{{F(x + 32)}}" y="{{F(y + 42)}}" class="label">dataset</text>""");
        sb.AppendLine($$"""<text x="{{F(x + 420)}}" y="{{F(y + 42)}}" class="label">rows</text>""");
        sb.AppendLine($$"""<text x="{{F(x + 520)}}" y="{{F(y + 42)}}" class="label">range (UTC)</text>""");
        sb.AppendLine($$"""<text x="{{F(x + 920)}}" y="{{F(y + 42)}}" class="label">status</text>""");
        sb.AppendLine($$"""<text x="{{F(x + 1040)}}" y="{{F(y + 42)}}" class="label">research use</text>""");

        var specs = new[]
        {
            (Datasets.RawAggTrades, "price, quantity, trade side, receive latency"),
            (Datasets.RawDepth, "raw bid/ask deltas and update ids"),
            (Datasets.BookChangeEvents, "per-level liquidity changes"),
            (Datasets.Features, "spread, midprice, OFI, L5 depth"),
            (Datasets.Snapshots, "book bootstrap and synchronization anchor")
        };

        for (var i = 0; i < specs.Length; i++)
        {
            var yy = y + 70 + i * rowH;
            sb.AppendLine($$"""<line x1="{{F(x + 24)}}" y1="{{F(yy - 22)}}" x2="{{F(x + frame.W - 24)}}" y2="{{F(yy - 22)}}" stroke="#24384f" stroke-width="1"/>""");
            var ds = specs[i].Item1;
            var rc = Count(rows, ds);
            DateTime? start = null;
            DateTime? end = null;
            switch (ds)
            {
                case Datasets.RawAggTrades:
                    if (sample.Trades.Count > 0)
                    {
                        start = sample.Trades.Min(t => t.TradeTs);
                        end = sample.Trades.Max(t => t.TradeTs);
                    }
                    break;
                case Datasets.RawDepth:
                    if (sample.Depth.Count > 0)
                    {
                        start = sample.Depth.Min(t => t.EventTs);
                        end = sample.Depth.Max(t => t.EventTs);
                    }
                    break;
                case Datasets.BookChangeEvents:
                    if (sample.Changes.Count > 0)
                    {
                        start = sample.Changes.Min(t => t.EventTs);
                        end = sample.Changes.Max(t => t.EventTs);
                    }
                    break;
                case Datasets.Features:
                    if (sample.Features.Count > 0)
                    {
                        start = sample.Features.Min(t => t.EventTs);
                        end = sample.Features.Max(t => t.EventTs);
                    }
                    break;
                case Datasets.Snapshots:
                    if (sample.Snapshots.Count > 0)
                    {
                        start = sample.Snapshots.Min(t => t.EventTs);
                        end = sample.Snapshots.Max(t => t.EventTs);
                    }
                    break;
            }

            var status = rc == 0 ? "MISSING" : (start.HasValue ? "OK" : "WARN");
            var rangeText = start.HasValue ? $"{start.Value.ToUniversalTime():yyyy-MM-dd HH:mm:ss} - {end.Value.ToUniversalTime():yyyy-MM-dd HH:mm:ss} UTC" : "n/a";

            sb.AppendLine($$"""<text x="{{F(x + 32)}}" y="{{F(yy)}}" class="legend">{{Escape(ds)}}</text>""");
            sb.AppendLine($$"""<text x="{{F(x + 420)}}" y="{{F(yy)}}" class="legend">{{rc}}</text>""");
            sb.AppendLine($$"""<text x="{{F(x + 520)}}" y="{{F(yy)}}" class="legend">{{Escape(rangeText)}}</text>""");
            sb.AppendLine($$"""<text x="{{F(x + 920)}}" y="{{F(yy)}}" class="legend">{{Escape(status)}}</text>""");
            sb.AppendLine($$"""<text x="{{F(x + 1040)}}" y="{{F(yy)}}" class="legend">{{Escape(specs[i].Item2)}}</text>""");
        }

        var latencyText = latency.Count == 0
            ? "latency summary: no latency samples"
            : "latency summary: " + string.Join(" | ", latency.Select(x => $"{x.Source} avg {N(x.AverageMs, "0.###")} ms, max {N(x.MaxMs, "0.###")} ms"));
        sb.AppendLine($$"""<text x="{{F(x + 32)}}" y="{{F(y + frame.H - 28)}}" class="tiny">{{Escape(latencyText)}}</text>""");
        return sb.ToString();
    }

    private static string RenderLineChart<T>(
        Frame frame,
        IReadOnlyList<T> rows,
        Func<T, DateTime> ts,
        string unit,
        string format,
        params LineSeries<T>[] series)
    {
        if (rows.Count == 0) return Empty(frame, "No rows were captured for this panel.");

        var values = rows
            .SelectMany(row => series.Select(s => s.Value(row)))
            .Where(x => !double.IsNaN(x) && !double.IsInfinity(x))
            .ToArray();
        if (values.Length == 0) return Empty(frame, "No valid numeric values were available.");

        var min = values.Min();
        var max = values.Max();
        ExpandRange(ref min, ref max);

        var ordered = rows.OrderBy(ts).ToArray();
        var minTs = ts(ordered.First());
        var maxTs = ts(ordered.Last());
        if (_globalMinTs.HasValue) minTs = _globalMinTs.Value;
        if (_globalMaxTs.HasValue) maxTs = _globalMaxTs.Value;
        var sb = new StringBuilder(Grid(frame));
        sb.Append(AxisLabels(frame, min, max, format));
        if (min < 0 && max > 0)
        {
            var zeroY = Y(0, min, max, frame);
            sb.AppendLine($$"""<line x1="{{F(frame.X)}}" y1="{{F(zeroY)}}" x2="{{F(frame.X + frame.W)}}" y2="{{F(zeroY)}}" stroke="#b6c2d1" stroke-width="2" opacity="0.5"/>""");
        }

        foreach (var s in series)
        {
            var path = PathFor(ordered, ts, s.Value, minTs, maxTs, min, max, frame);
            if (path.Length > 0)
                sb.AppendLine($$"""<path d="{{path}}" fill="none" stroke="{{s.Color}}" stroke-width="4" stroke-linejoin="round" stroke-linecap="round"/>""");
        }

        sb.Append(Legend(frame, series.Select(x => (x.Label, x.Color)).ToArray()));
        sb.AppendLine($$"""<text x="{{F(frame.X + frame.W - 12)}}" y="{{F(frame.Y + frame.H + 30)}}" text-anchor="end" class="tiny">{{Escape(unit)}}</text>""");
        sb.Append(AxisTimeLabels(frame, minTs, maxTs));
        return sb.ToString();
    }

    private static string Grid(Frame frame)
    {
        var sb = new StringBuilder();
        sb.AppendLine($$"""<rect x="{{F(frame.X)}}" y="{{F(frame.Y)}}" width="{{F(frame.W)}}" height="{{F(frame.H)}}" rx="12" fill="#0b1627" stroke="#263c56" stroke-width="2"/>""");
        for (var i = 1; i < 4; i++)
        {
            var y = frame.Y + frame.H * i / 4.0;
            sb.AppendLine($$"""<line x1="{{F(frame.X)}}" y1="{{F(y)}}" x2="{{F(frame.X + frame.W)}}" y2="{{F(y)}}" class="grid"/>""");
        }

        for (var i = 1; i < 6; i++)
        {
            var x = frame.X + frame.W * i / 6.0;
            sb.AppendLine($$"""<line x1="{{F(x)}}" y1="{{F(frame.Y)}}" x2="{{F(x)}}" y2="{{F(frame.Y + frame.H)}}" class="grid"/>""");
        }

        sb.AppendLine($$"""<line x1="{{F(frame.X)}}" y1="{{F(frame.Y + frame.H)}}" x2="{{F(frame.X + frame.W)}}" y2="{{F(frame.Y + frame.H)}}" class="axis"/>""");
        sb.AppendLine($$"""<line x1="{{F(frame.X)}}" y1="{{F(frame.Y)}}" x2="{{F(frame.X)}}" y2="{{F(frame.Y + frame.H)}}" class="axis"/>""");
        return sb.ToString();
    }

    private static string AxisLabels(Frame frame, double min, double max, string format)
    {
        var sb = new StringBuilder();
        for (var i = 0; i <= 4; i++)
        {
            var value = max - (max - min) * i / 4.0;
            var y = frame.Y + frame.H * i / 4.0 + 5;
            sb.AppendLine($$"""<text x="{{F(frame.X - 14)}}" y="{{F(y)}}" text-anchor="end" class="tick">{{N(value, format)}}</text>""");
        }

        return sb.ToString();
    }

    private static string Legend(Frame frame, params (string Label, string Color)[] items)
    {
        var sb = new StringBuilder();
        var x = frame.X + 22;
        var y = frame.Y - 14;
        foreach (var item in items)
        {
            sb.AppendLine($$"""<line x1="{{F(x)}}" y1="{{F(y)}}" x2="{{F(x + 34)}}" y2="{{F(y)}}" stroke="{{item.Color}}" stroke-width="5" stroke-linecap="round"/>""");
            sb.AppendLine($$"""<text x="{{F(x + 46)}}" y="{{F(y + 6)}}" class="legend">{{Escape(item.Label)}}</text>""");
            x += Math.Max(180, item.Label.Length * 12 + 90);
        }

        return sb.ToString();
    }

    private static string Empty(Frame frame, string message) =>
        $$"""
<rect x="{{F(frame.X)}}" y="{{F(frame.Y)}}" width="{{F(frame.W)}}" height="{{F(frame.H)}}" rx="12" fill="#0b1627" stroke="#263c56" stroke-width="2"/>
<text x="{{F(frame.X + frame.W / 2)}}" y="{{F(frame.Y + frame.H / 2)}}" text-anchor="middle" class="subtitle">{{Escape(message)}}</text>
""";

    private static string PathFor<T>(
        IReadOnlyList<T> rows,
        Func<T, DateTime> ts,
        Func<T, double> value,
        DateTime minTs,
        DateTime maxTs,
        double min,
        double max,
        Frame frame)
    {
        var sb = new StringBuilder();
        foreach (var row in rows)
        {
            var v = value(row);
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;

            var x = X(ts(row), minTs, maxTs, frame);
            var y = Y(v, min, max, frame);
            sb.Append(sb.Length == 0 ? "M " : " L ");
            sb.Append(F(x));
            sb.Append(' ');
            sb.Append(F(y));
        }

        return sb.ToString();
    }

    private static IReadOnlyList<Candle> BuildCandles(IReadOnlyList<RawAggTradeRow> trades, int maxBuckets)
    {
        var ordered = trades.OrderBy(x => x.TradeTs).ToArray();
        var bucketCount = Math.Clamp(ordered.Length / 12, 8, maxBuckets);
        return BucketByTime(ordered, x => x.TradeTs, bucketCount)
            .Where(x => x.Count > 0)
            .Select(bucket => new Candle(
                Open: bucket.First().Price,
                High: bucket.Max(x => x.Price),
                Low: bucket.Min(x => x.Price),
                Close: bucket.Last().Price,
                Volume: bucket.Sum(x => x.Quantity)))
            .ToArray();
    }

    private static IReadOnlyList<TradeBucket> BuildTradeBuckets(IReadOnlyList<RawAggTradeRow> trades, int maxBuckets)
    {
        var ordered = trades.OrderBy(x => x.TradeTs).ToArray();
        var bucketCount = Math.Clamp(ordered.Length / 10, 8, maxBuckets);
        return BucketByTime(ordered, x => x.TradeTs, bucketCount)
            .Where(x => x.Count > 0)
            .Select(bucket => new TradeBucket(
                bucket[0].TradeTs,
                bucket.Where(x => x.TradeSide == "buy").Sum(x => x.Quantity),
                bucket.Where(x => x.TradeSide == "sell").Sum(x => x.Quantity)))
            .ToArray();
    }

    private static IReadOnlyList<ChangeBucket> BuildChangeBuckets(IReadOnlyList<BookChangeRow> changes, int maxBuckets)
    {
        var ordered = changes.OrderBy(x => x.EventTs).ToArray();
        var bucketCount = Math.Clamp(ordered.Length / 10, 8, maxBuckets);
        return BucketByTime(ordered, x => x.EventTs, bucketCount)
            .Where(x => x.Count > 0)
            .Select(bucket => new ChangeBucket(
                bucket[0].EventTs,
                bucket.Count(x => x.EventType == "limit_add"),
                bucket.Count(x => x.EventType != "limit_add")))
            .ToArray();
    }

    private static List<T>[] BucketByTime<T>(IReadOnlyList<T> rows, Func<T, DateTime> ts, int bucketCount)
    {
        var buckets = Enumerable.Range(0, Math.Max(1, bucketCount)).Select(_ => new List<T>()).ToArray();
        if (rows.Count == 0) return buckets;

        var minTs = ts(rows.First());
        var maxTs = ts(rows.Last());
        var spanMs = Math.Max(1, (maxTs - minTs).TotalMilliseconds);
        foreach (var row in rows)
        {
            var index = Math.Min(buckets.Length - 1, (int)(((ts(row) - minTs).TotalMilliseconds / spanMs) * buckets.Length));
            buckets[index].Add(row);
        }

        return buckets;
    }

    private static Frame ChartFrame(int panelY) =>
        new(ContentX + 96, panelY + 112, ContentWidth - 142, PanelHeight - 158);

    private static Frame ChartFrameAt(int panelY, double panelX, double panelW) =>
        new(panelX + 24, panelY + 112, panelW - 48, PanelHeight - 158);

    private static string MetricCard(int x, int y, int width, string label, string value, string color) =>
        $$"""
  <rect x="{{x}}" y="{{y}}" width="{{width}}" height="130" rx="24" fill="#101d31" stroke="#2c425e" stroke-width="2" filter="url(#shadow)"/>
  <text x="{{x + 28}}" y="{{y + 56}}" class="metric" fill="{{color}}">{{Escape(value)}}</text>
  <text x="{{x + 28}}" y="{{y + 96}}" class="label">{{Escape(label.ToUpperInvariant())}}</text>
""";

    private static double X(DateTime ts, DateTime minTs, DateTime maxTs, Frame frame)
    {
        var span = Math.Max(1, (maxTs - minTs).TotalMilliseconds);
        return frame.X + ((ts - minTs).TotalMilliseconds / span) * frame.W;
    }

    private static double Y(double value, double min, double max, Frame frame) =>
        frame.Y + (max - value) / Math.Max(0.000001, max - min) * frame.H;

    private static void ExpandRange(ref double min, ref double max)
    {
        if (Math.Abs(max - min) < 0.000001)
        {
            min -= 1;
            max += 1;
            return;
        }

        var pad = (max - min) * 0.08;
        min -= pad;
        max += pad;
    }

    private static void ExpandRangeFromZero(ref double max)
    {
        if (max < 0.000001) max = 1;
        max *= 1.12;
    }

    private static string AxisTimeLabels(Frame frame, DateTime minTs, DateTime maxTs)
    {
        var sb = new StringBuilder();
        var span = maxTs - minTs;
        var tickCount = 5;
        var format = span.TotalDays >= 1 ? "yyyy-MM-dd HH:mm" : "HH:mm:ss";
        for (var i = 0; i < tickCount; i++)
        {
            var t = minTs + TimeSpan.FromTicks((long)((double)i / (tickCount - 1) * span.Ticks));
            var x = X(t, minTs, maxTs, frame);
            sb.AppendLine($$"""<line x1="{{F(x)}}" y1="{{F(frame.Y + frame.H)}}" x2="{{F(x)}}" y2="{{F(frame.Y + frame.H + 8)}}" stroke="#2f4b62" stroke-width="2"/>""");
            sb.AppendLine($$"""<text x="{{F(x)}}" y="{{F(frame.Y + frame.H + 30)}}" text-anchor="middle" class="tick">{{t.ToString(format, CultureInfo.InvariantCulture)}}</text>""");
        }

        return sb.ToString();
    }

    private static IReadOnlyList<T> Downsample<T>(IReadOnlyList<T> rows, Func<T, DateTime> tsSelector, int maxPoints)
    {
        if (rows == null) return Array.Empty<T>();
        if (rows.Count <= maxPoints) return rows;
        var ordered = rows.OrderBy(tsSelector).ToArray();
        var n = ordered.Length;
        var start = Math.Max(0, n - maxPoints);
        return ordered.Skip(start).ToArray();
    }

    private static int Count(IReadOnlyDictionary<string, int> rows, string dataset) =>
        rows.TryGetValue(dataset, out var count) ? count : 0;

    private static string F(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string N(double value, string format) =>
        value.ToString(format, CultureInfo.InvariantCulture);

    private static string Escape(string value) =>
        value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal);

    private sealed record Frame(double X, double Y, double W, double H);
    private sealed record LineSeries<T>(string Label, string Color, Func<T, double> Value);
    private sealed record Candle(double Open, double High, double Low, double Close, double Volume);
    private sealed record TradeBucket(DateTime Ts, double Buy, double Sell);
    private sealed record ChangeBucket(DateTime Ts, double LimitAdds, double Cancels);
    private sealed record LatencyPoint(DateTime Ts, double Value, string Source);
}
