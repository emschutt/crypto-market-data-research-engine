from __future__ import annotations

import asyncio
import shutil
import sys
from pathlib import Path

PROJECT_ROOT = Path(__file__).resolve().parents[1]
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

from src.collectors import run_mock_capture
from src.config import PipelineConfig
from src.diagnostics import WebSocketLatencyTracker
from src.export import build_research_charts
from src.storage import MarketDataStore, read_dataset


async def run() -> None:
    output_path = Path("sample_data/smoke")
    charts_path = Path("charts")
    if output_path.exists():
        shutil.rmtree(output_path)

    config = PipelineConfig(
        symbol="BTCUSDT",
        output_path=output_path,
        capture_duration_seconds=1.2,
        dataset_type="all",
        mode="mock",
        bar_ms=100,
        file_format="parquet",
    )
    latency = WebSocketLatencyTracker()
    store = MarketDataStore(
        config.output_path,
        config.symbol_upper,
        config.file_format,
        dataset_type=config.dataset_type,
    )

    counts = await run_mock_capture(config, store, latency)
    assert counts["raw_agg_trades"] > 0
    assert counts["raw_depth"] > 0
    assert counts["features"] > 0

    trades = read_dataset(output_path, "raw_agg_trades")
    depth = read_dataset(output_path, "raw_depth")
    features = read_dataset(output_path, "features")

    expected_trade_columns = {"event_ts", "trade_id", "price", "quantity", "buyer_is_maker", "trade_side"}
    expected_depth_columns = {"event_ts", "first_update_id", "final_update_id", "bids_json", "asks_json"}
    expected_feature_columns = {"bar_ts", "midprice", "spread", "order_flow_imbalance", "trade_imbalance"}

    assert expected_trade_columns.issubset(trades.columns), trades.columns
    assert expected_depth_columns.issubset(depth.columns), depth.columns
    assert expected_feature_columns.issubset(features.columns), features.columns

    chart = build_research_charts(output_path, charts_path)["chart"]

    print("SMOKE TEST PASSED")
    print(f"output_path={output_path}")
    print(f"datasets_written={counts}")
    print(f"latency={latency.as_dict()}")
    print(f"chart={chart}")


if __name__ == "__main__":
    asyncio.run(run())
