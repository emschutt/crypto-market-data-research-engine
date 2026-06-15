namespace CryptoMarketDataResearchEngine.Models;

public static class Datasets
{
    public const string RawDepth = "raw_depth";
    public const string RawAggTrades = "raw_agg_trades";
    public const string BookChangeEvents = "book_change_events";
    public const string Features = "features";
    public const string Snapshots = "snapshots";

    public static readonly string[] All =
    [
        RawDepth,
        RawAggTrades,
        BookChangeEvents,
        Features,
        Snapshots
    ];

    public const string Exchange = "binance";
}
