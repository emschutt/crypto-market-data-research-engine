# crypto-market-data-research-engine

Standalone C#/.NET market-data pipeline for collecting Binance high-frequency crypto data, treating it into research-ready microstructure features, and storing it as Parquet.

This repository is intentionally focused on data engineering and quant research. It contains only public market-data extraction, treatment, diagnostics, and storage code.

![Generated market data dashboard](charts/market-data-pipeline-dashboard.svg)

## What It Does

The project captures Binance market data at event level and writes typed datasets that can be used for market microstructure research.

- Collects Binance `aggTrade` events from WebSocket.
- Collects Binance order book updates from the high-frequency `depth@100ms` WebSocket stream.
- Bootstraps the local book from the Binance REST depth snapshot.
- Reconstructs an L5 local order book.
- Writes raw depth frames, raw aggregate trades, book-change rows, derived features, and snapshots.
- Stores each dataset as hive-partitioned Parquet.
- Tracks receive latency for WebSocket diagnostics.
- Produces a large centered SVG dashboard with stacked time-series panels, OHLC candles, volume, spread, imbalance, depth, book-change mix, latency, and dataset health.
- Includes a smoke test that runs the full mock pipeline, writes Parquet, reads it back, validates columns, and exits successfully.

## Architecture

```mermaid
flowchart LR
    A[Binance REST snapshot] --> B[L5 book bootstrap]
    C[Binance WebSocket aggTrade] --> D[Event parser]
    E[Binance WebSocket depth@100ms] --> D
    D --> F[Raw capture service]
    D --> G[Treatment engine]
    G --> H[L5 book change rows]
    G --> I[Feature rows]
    F --> J[Parquet writer]
    H --> J
    I --> J
    B --> J
    J --> K[Hive-partitioned research datasets]
    J --> L[Dashboard SVG]
```

The live collector connects to:

```text
wss://stream.binance.com:9443/stream?streams=btcusdt@depth@100ms/btcusdt@aggTrade
```

The raw capture path is event-triggered, not timer-sampled:

- `raw_agg_trades`: one row per Binance aggregate trade event.
- `raw_depth`: one row per Binance depth update frame.
- `book_change_events`: one row per changed bid or ask level inside the processed L5 book update.
- `features`: one row per depth event by default, or throttled by `--feature-interval-ms`.
- `snapshots`: one row when the collector bootstraps the local book from REST.

## Repository Layout

```text
crypto-market-data-research-engine/
├── README.md
├── crypto-market-data-research-engine.sln
├── src/
│   └── CryptoMarketDataResearchEngine/
│       ├── Collectors/
│       ├── Configuration/
│       ├── Diagnostics/
│       ├── Export/
│       ├── Models/
│       ├── Storage/
│       ├── Treatment/
│       ├── PipelineRunner.cs
│       └── Program.cs
├── tests/
│   └── CryptoMarketDataResearchEngine.SmokeTests/
├── sample_data/
│   └── smoke/
├── charts/
│   └── market-data-pipeline-dashboard.svg
├── .env.example
└── .gitignore
```

## Main Components

`Collectors/BinanceWebSocketCollector.cs`

Live Binance ingestion. It starts with a REST depth snapshot, opens a Binance combined WebSocket stream, parses `aggTrade` and `depthUpdate` messages, records receive latency, and sends typed rows to storage and treatment.

`Collectors/MockBinanceCollector.cs`

Deterministic local collector used by the smoke test. It simulates high-frequency trades and depth updates so the project can be validated without depending on Binance availability.

`Treatment/BookTreatmentEngine.cs`

Maintains a local order book and creates research features from the event stream. It computes best bid, best ask, midprice, spread, microprice, L5 depth, order-flow imbalance, rolling buy/sell volume, trade imbalance, and rolling add/cancel pressure.

`Storage/ParquetMarketDataWriter.cs`

Writes typed datasets to Parquet. Files are partitioned by dataset, symbol, UTC date, and UTC hour.

`Diagnostics/WebSocketLatencyTracker.cs`

Tracks event timestamp versus local receive timestamp so live runs can report WebSocket receive-latency summaries.

`Export/DashboardSvgRenderer.cs`

Generates the large dashboard image at `charts/market-data-pipeline-dashboard.svg` after collection. The dashboard is built from the same captured rows that are sent to Parquet, using a bounded in-memory sample so the visualization stays tied to the actual pipeline output.

## Dashboard

The generated SVG is intentionally arranged as a single centered research dashboard instead of a two-column grid. All time-series panels are stacked vertically so each chart has the same width and comparable time axis.

Dashboard panels:

- OHLC candlesticks built from aggregate trade prices.
- Midprice, best bid, and best ask from the treated L5 book.
- Buy/sell aggregate trade volume.
- Bid-ask spread.
- Book order-flow imbalance and rolling trade imbalance.
- Top-5 bid and ask depth.
- Book change mix: limit additions versus cancel-or-trade deltas.
- WebSocket latency diagnostics for aggregate trades and depth updates.
- Dataset health table with row counts and latency summary.

Latency interpretation:

- In mock mode, latency is deterministic synthetic jitter with occasional spikes. It should be bounded and irregular, not a perfect sawtooth.
- In live mode, latency is measured as local receive timestamp minus Binance event or trade timestamp. Real values should have jitter and occasional spikes due to network, exchange timestamping, local scheduling, and message batching.
- A perfectly repeating latency pattern in mock data is usually a generator artifact, not a live-market property.

## Datasets

Data is written under:

```text
<output>/<dataset>/symbol=<SYMBOL>/date_utc=<YYYY-MM-DD>/hour_utc=<HH>/part-*.parquet
```

Datasets:

- `raw_agg_trades`: aggregate trade id, price, quantity, maker side, inferred buy/sell side, event time, trade time, receive latency, optional raw payload.
- `raw_depth`: first update id, last update id, bid update JSON, ask update JSON, update counts, receive latency, optional raw payload.
- `book_change_events`: side, price, previous quantity, new quantity, delta quantity, event type, best-level flag, update ids.
- `features`: best bid, best ask, midprice, spread, microprice, L5 depth, order-flow imbalance, trade imbalance, rolling add/cancel quantities.
- `snapshots`: REST depth snapshot metadata and serialized top book levels.

Each Parquet file also gets a small `.meta.json` sidecar with dataset name, row count, symbol, exchange, and write timestamp.

## Requirements

- .NET SDK 10.0 or newer.
- Internet access for live Binance collection.
- No API key is needed for public Binance market data.

Check your .NET SDK:

```bash
dotnet --version
```

## Configuration

Command-line options can be passed directly:

```bash
dotnet run --project src/CryptoMarketDataResearchEngine -- collect \
  --mode mock \
  --symbol BTCUSDT \
  --duration 3 \
  --output sample_data/smoke \
  --dataset all \
  --feature-interval-ms 0 \
  --rolling-window-ms 1000
```

The same values can be supplied through environment variables:

```text
SYMBOL=BTCUSDT
MODE=mock
OUTPUT_PATH=sample_data/smoke
CAPTURE_DURATION_SECONDS=3
DATASET_TYPE=all
REST_DEPTH_LIMIT=1000
FEATURE_INTERVAL_MS=0
ROLLING_WINDOW_MS=1000
RAW_PAYLOAD=false
```

Important options:

- `--mode mock`: generate deterministic local market data for tests and demos.
- `--mode live`: connect to Binance public WebSocket and REST depth snapshot.
- `--symbol BTCUSDT`: symbol to collect.
- `--duration 30`: capture duration in seconds.
- `--output data/binance`: output root path.
- `--dataset all`: write all datasets, or choose one dataset name.
- `--feature-interval-ms 0`: emit one feature row per depth event. Set a positive value to downsample only derived features.
- `--rolling-window-ms 1000`: rolling window used for trade and book-change features.
- `--raw-payload true`: store raw JSON payloads. Default is false to keep files smaller.

## Run The Smoke Test

The smoke test uses the mock collector, writes Parquet sample data, reads every dataset back, checks required columns, and generates the dashboard image.

```bash
dotnet run --project tests/CryptoMarketDataResearchEngine.SmokeTests
```

Passing result from this local run:

```text
SMOKE TEST PASSED
output_path=sample_data/smoke
rows_written={"raw_depth":300,"raw_agg_trades":300,"book_change_events":300,"features":300,"snapshots":1}
chart=charts/market-data-pipeline-dashboard.svg
```

Build verification:

```bash
dotnet build crypto-market-data-research-engine.sln
```

Passing result from this local run:

```text
Build succeeded.
0 Warning(s)
0 Error(s)
```

## Run A Mock Collection

```bash
dotnet run --project src/CryptoMarketDataResearchEngine -- collect \
  --mode mock \
  --symbol BTCUSDT \
  --duration 10 \
  --output sample_data/mock_run \
  --mock-events-per-second 250
```

This writes synthetic but structurally realistic data. It is useful for validating downstream readers, dashboards, and schema contracts.

## Run A Live Binance Collection

```bash
dotnet run --project src/CryptoMarketDataResearchEngine -- collect \
  --mode live \
  --symbol BTCUSDT \
  --duration 30 \
  --output data/binance \
  --feature-interval-ms 0
```

Live mode uses public Binance endpoints only. It does not need credentials.

For longer runs, write to `data/binance` or another local path that is ignored by Git:

```bash
dotnet run --project src/CryptoMarketDataResearchEngine -- collect \
  --mode live \
  --symbol ETHUSDT \
  --duration 300 \
  --output data/binance_eth \
  --raw-payload false
```

## Inspect Written Parquet

```bash
dotnet run --project src/CryptoMarketDataResearchEngine -- inspect --output sample_data/smoke
```

The inspect command lists each dataset, the number of Parquet files, row groups, and detected columns.

## Research Ideas

This project is built to support market microstructure analysis and public data research.

- Trade volume over time from `raw_agg_trades.quantity`.
- Buy versus sell volume using `trade_side`.
- Bid-ask spread from `features.spread`.
- Midprice and microprice drift from `features.midprice` and `features.microprice`.
- L5 liquidity imbalance from `total_bid_depth_l5` and `total_ask_depth_l5`.
- Order-flow imbalance from `features.order_flow_imbalance`.
- Short-window trade imbalance from `features.trade_imbalance`.
- Limit add pressure and cancel pressure from the rolling add/cancel feature columns.
- Latency analysis using `receive_latency_ms` in raw trade and depth datasets.
- Data quality checks using update id continuity, empty side updates, duplicate timestamps, and missing book levels.

## Design Notes

The pipeline is intentionally concise:

- Raw market events are written at event level.
- Derived feature frequency is configurable.
- Row models are explicit C# records.
- Storage schemas are explicit Parquet columns.
- Live and mock collectors share the same sink and treatment path.
- Sample data is small enough to keep in Git.
- Large or long-running captures should stay under ignored `data/` paths.

## Scope Boundaries

The standalone repository is limited to market-data research infrastructure:

- Public Binance market-data collection.
- Event-level parsing and row models.
- L5 book treatment.
- Research feature generation.
- Parquet storage.
- Diagnostics and sample visualization.
- No private keys, local secret files, or account credentials.

## Limitations

- Binance depth updates are collected from `depth@100ms`, which is Binance's high-frequency public diff-depth stream for this endpoint.
- `aggTrade` is aggregate trade data, not private order-level data.
- The current writer flushes at the end of a capture run. Very long production captures should add periodic flush rotation.
- The sample dashboard is generated as SVG for portability and GitHub rendering.
