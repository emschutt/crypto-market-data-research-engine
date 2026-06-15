using System.Globalization;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using CryptoMarketDataResearchEngine.Configuration;
using CryptoMarketDataResearchEngine.Diagnostics;
using CryptoMarketDataResearchEngine.Models;
using CryptoMarketDataResearchEngine.Storage;
using CryptoMarketDataResearchEngine.Treatment;

namespace CryptoMarketDataResearchEngine.Collectors;

public sealed class BinanceWebSocketCollector : IMarketDataCollector
{
    private readonly CaptureOptions _options;
    private readonly IMarketDataSink _sink;
    private readonly WebSocketLatencyTracker _latency;
    private readonly BookTreatmentEngine _book;
    private readonly HttpClient _http = new();

    public BinanceWebSocketCollector(CaptureOptions options, IMarketDataSink sink, WebSocketLatencyTracker latency)
    {
        _options = options;
        _sink = sink;
        _latency = latency;
        _book = new BookTreatmentEngine(options);
    }

    public async Task<CaptureResult> RunAsync(CancellationToken ct = default)
    {
        await BootstrapBookAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.CaptureDurationSeconds));

        var url = $"wss://stream.binance.com:9443/stream?streams={_options.SymbolLower}@depth@100ms/{_options.SymbolLower}@aggTrade";
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(url), timeout.Token);

        var buffer = new byte[128 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !timeout.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, timeout.Token);
                    if (result.Count > 0)
                        ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage && result.MessageType == WebSocketMessageType.Text);

                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;

                ProcessMessage(Encoding.UTF8.GetString(ms.ToArray()));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && timeout.IsCancellationRequested)
        {
            // Expected path when the requested capture duration expires while ReceiveAsync is blocked.
        }

        var rows = await _sink.FlushAsync(CancellationToken.None);
        return new CaptureResult(_options.Mode, _options.SymbolUpper, Path.GetFullPath(_options.OutputPath), rows, _latency.Summaries(), []);
    }

    private async Task BootstrapBookAsync(CancellationToken ct)
    {
        var url = $"https://api.binance.com/api/v3/depth?symbol={_options.SymbolUpper}&limit={_options.RestDepthLimit}";
        var snapshot = await _http.GetFromJsonAsync<DepthSnapshot>(url, ct)
            ?? throw new InvalidOperationException("Binance depth snapshot returned no data.");

        var now = DateTime.UtcNow;
        var bids = snapshot.Bids.Select(ParseLevel).Where(x => x.Size > 0).ToArray();
        var asks = snapshot.Asks.Select(ParseLevel).Where(x => x.Size > 0).ToArray();
        _sink.Enqueue(_book.ApplySnapshot(snapshot.LastUpdateId, bids, asks, now));
    }

    private void ProcessMessage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var data = doc.RootElement.TryGetProperty("data", out var dataElement)
            ? dataElement
            : doc.RootElement;

        if (!data.TryGetProperty("e", out var eventTypeElement))
            return;

        switch (eventTypeElement.GetString())
        {
            case "depthUpdate":
                ProcessDepth(data, json);
                break;
            case "aggTrade":
                ProcessAggTrade(data, json);
                break;
        }
    }

    private void ProcessDepth(JsonElement data, string rawJson)
    {
        var localTs = DateTime.UtcNow;
        var eventTs = FromUnixMs(data.GetProperty("E").GetInt64());
        var firstUpdateId = data.GetProperty("U").GetInt64();
        var lastUpdateId = data.GetProperty("u").GetInt64();
        var bids = ParseLevels(data.GetProperty("b")).ToArray();
        var asks = ParseLevels(data.GetProperty("a")).ToArray();

        _sink.Enqueue(new RawDepthRow(
            EventTs: eventTs,
            LocalReceiveTs: localTs,
            Symbol: _options.SymbolUpper,
            FirstUpdateId: firstUpdateId,
            LastUpdateId: lastUpdateId,
            BidUpdatesJson: JsonSerializer.Serialize(bids),
            AskUpdatesJson: JsonSerializer.Serialize(asks),
            BidUpdateCount: bids.Length,
            AskUpdateCount: asks.Length,
            ReceiveLatencyMs: _latency.Record("binance.depth", eventTs, localTs),
            RawPayloadJson: _options.RawPayload ? rawJson : ""));

        foreach (var row in _book.ApplyDepthUpdate(eventTs, localTs, firstUpdateId, lastUpdateId, bids, asks))
            _sink.Enqueue(row);

        if (_book.BuildFeature(eventTs) is { } feature)
            _sink.Enqueue(feature);
    }

    private void ProcessAggTrade(JsonElement data, string rawJson)
    {
        var localTs = DateTime.UtcNow;
        var eventTs = FromUnixMs(data.GetProperty("E").GetInt64());
        var tradeTs = FromUnixMs(data.GetProperty("T").GetInt64());
        var buyerIsMaker = data.GetProperty("m").GetBoolean();
        var row = new RawAggTradeRow(
            EventTs: eventTs,
            TradeTs: tradeTs,
            LocalReceiveTs: localTs,
            Symbol: _options.SymbolUpper,
            AggTradeId: data.GetProperty("a").GetInt64(),
            FirstTradeId: data.GetProperty("f").GetInt64(),
            LastTradeId: data.GetProperty("l").GetInt64(),
            Price: ParseDouble(data.GetProperty("p")),
            Quantity: ParseDouble(data.GetProperty("q")),
            BuyerIsMaker: buyerIsMaker,
            TradeSide: buyerIsMaker ? "sell" : "buy",
            ReceiveLatencyMs: _latency.Record("binance.aggTrade", tradeTs, localTs),
            RawPayloadJson: _options.RawPayload ? rawJson : "");
        _sink.Enqueue(row);
        _book.RecordTrade(row);
    }

    private static IEnumerable<Level> ParseLevels(JsonElement levels)
    {
        foreach (var level in levels.EnumerateArray())
        {
            yield return new Level(ParseDouble(level[0]), ParseDouble(level[1]));
        }
    }

    private static Level ParseLevel(string[] level) => new(ParseDouble(level[0]), ParseDouble(level[1]));

    private static double ParseDouble(JsonElement element) =>
        double.Parse(element.GetString() ?? "0", CultureInfo.InvariantCulture);

    private static double ParseDouble(string value) =>
        double.Parse(value, CultureInfo.InvariantCulture);

    private static DateTime FromUnixMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    private sealed class DepthSnapshot
    {
        public long LastUpdateId { get; set; }
        public string[][] Bids { get; set; } = [];
        public string[][] Asks { get; set; } = [];
    }
}
