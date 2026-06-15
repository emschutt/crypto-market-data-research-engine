from __future__ import annotations

import argparse
import asyncio
import json
from pathlib import Path

from src.collectors import run_live_capture, run_mock_capture
from src.config import PipelineConfig
from src.diagnostics import WebSocketLatencyTracker
from src.export.visualize import build_research_charts
from src.storage import MarketDataStore


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="crypto-market-data-research-engine",
        description="Collect Binance high-frequency market data for research.",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    collect = sub.add_parser("collect", help="Run the Binance collector")
    collect.add_argument("--symbol", default="BTCUSDT")
    collect.add_argument("--output", default="data/binance")
    collect.add_argument("--duration", type=float, default=10.0)
    collect.add_argument(
        "--dataset",
        default="all",
        choices=["all", "raw_depth", "raw_agg_trades", "book_change_events", "snapshots", "features"],
    )
    collect.add_argument("--mode", default="mock", choices=["mock", "live"])
    collect.add_argument("--bar-ms", type=int, default=100)
    collect.add_argument("--format", default="parquet", choices=["parquet", "csv"])

    analyze = sub.add_parser("analyze", help="Build research charts from collected data")
    analyze.add_argument("--input", default="sample_data/smoke")
    analyze.add_argument("--charts", default="charts")

    return parser


async def collect_async(args: argparse.Namespace) -> dict:
    config = PipelineConfig(
        symbol=args.symbol,
        output_path=Path(args.output),
        capture_duration_seconds=args.duration,
        dataset_type=args.dataset,
        mode=args.mode,
        bar_ms=args.bar_ms,
        file_format=args.format,
    )
    latency = WebSocketLatencyTracker()
    store = MarketDataStore(
        config.output_path,
        config.symbol_upper,
        config.file_format,
        dataset_type=config.dataset_type,
    )

    if config.mode == "mock":
        counts = await run_mock_capture(config, store, latency)
    else:
        counts = await run_live_capture(config, store, latency)

    result = {
        "mode": config.mode,
        "symbol": config.symbol_upper,
        "output_path": str(config.output_path),
        "datasets_written": counts,
        "latency": latency.as_dict(),
    }
    return result


def main() -> None:
    parser = build_parser()
    args = parser.parse_args()

    if args.command == "collect":
        result = asyncio.run(collect_async(args))
        print(json.dumps(result, indent=2))
        return

    if args.command == "analyze":
        output = build_research_charts(Path(args.input), Path(args.charts))
        print(json.dumps(output, indent=2))
        return

    parser.error("Unknown command")


if __name__ == "__main__":
    main()

