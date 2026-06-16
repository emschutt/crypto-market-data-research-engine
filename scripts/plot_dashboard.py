#!/usr/bin/env python3
"""Render a clean matplotlib dashboard from the Parquet datasets.

The ingestion and treatment pipeline is C#/.NET. This script is intentionally
only a visualization layer for reviewer-friendly static charts.
"""

from __future__ import annotations

import argparse
from pathlib import Path

import matplotlib.dates as mdates
import matplotlib.pyplot as plt
import numpy as np
import pandas as pd


DATASETS = {
    "trades": "raw_agg_trades",
    "depth": "raw_depth",
    "changes": "book_change_events",
    "features": "features",
    "snapshots": "snapshots",
}


def read_dataset(root: Path, dataset: str) -> pd.DataFrame:
    files = sorted((root / dataset).glob("**/*.parquet"))
    if not files:
        return pd.DataFrame()
    return pd.concat((pd.read_parquet(path) for path in files), ignore_index=True)


def as_utc(series: pd.Series) -> pd.Series:
    return pd.to_datetime(series, unit="ms", utc=True)


def build_ohlc(trades: pd.DataFrame, buckets: int = 32) -> pd.DataFrame:
    if trades.empty:
        return pd.DataFrame()

    frame = trades.sort_values("trade_ts_ms").copy()
    frame["ts"] = as_utc(frame["trade_ts_ms"])
    frame = frame.set_index("ts")
    bucket_ms = max(1, int((frame.index.max() - frame.index.min()).total_seconds() * 1000 / buckets))
    rule = f"{bucket_ms}ms"
    ohlc = frame["price"].resample(rule).ohlc().dropna()
    volume = frame["quantity"].resample(rule).sum().reindex(ohlc.index).fillna(0)
    ohlc["volume"] = volume
    return ohlc


def aggregate_volume(trades: pd.DataFrame, buckets: int = 48) -> pd.DataFrame:
    if trades.empty:
        return pd.DataFrame()

    frame = trades.sort_values("trade_ts_ms").copy()
    frame["ts"] = as_utc(frame["trade_ts_ms"])
    frame = frame.set_index("ts")
    bucket_ms = max(1, int((frame.index.max() - frame.index.min()).total_seconds() * 1000 / buckets))
    grouped = (
        frame.pivot_table(index=frame.index, columns="trade_side", values="quantity", aggfunc="sum")
        .resample(f"{bucket_ms}ms")
        .sum()
        .fillna(0)
    )
    for side in ["buy", "sell"]:
        if side not in grouped:
            grouped[side] = 0.0
    return grouped[["buy", "sell"]]


def style_axis(ax, ylabel: str) -> None:
    ax.set_ylabel(ylabel, color="#d8e2ee")
    ax.set_xlabel("Time (UTC)", color="#d8e2ee")
    ax.grid(True, color="#26394f", linewidth=0.7, alpha=0.72)
    ax.tick_params(colors="#b7c4d4", labelsize=9)
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M:%S"))
    ax.xaxis.set_major_locator(mdates.AutoDateLocator(minticks=4, maxticks=7))
    for spine in ax.spines.values():
        spine.set_color("#344a63")


def draw_candles(ax, ohlc: pd.DataFrame) -> None:
    if ohlc.empty:
        ax.text(0.5, 0.5, "No trade data", transform=ax.transAxes, ha="center", va="center")
        return

    dates = mdates.date2num(ohlc.index.to_pydatetime())
    width = (dates[1] - dates[0]) * 0.55 if len(dates) > 1 else 0.000002
    for date, row in zip(dates, ohlc.itertuples()):
        up = row.close >= row.open
        color = "#2dd4bf" if up else "#fb7185"
        ax.vlines(date, row.low, row.high, color=color, linewidth=1.4)
        body_low = min(row.open, row.close)
        body_height = max(abs(row.close - row.open), 0.01)
        ax.add_patch(
            plt.Rectangle(
                (date - width / 2, body_low),
                width,
                body_height,
                facecolor=color,
                edgecolor=color,
                alpha=0.88,
            )
        )
    ax.set_xlim(dates.min() - width, dates.max() + width)


def main() -> int:
    parser = argparse.ArgumentParser(description="Render a clean market-data dashboard from Parquet outputs.")
    parser.add_argument("--input", default="sample_data/smoke", help="Root path containing dataset partitions.")
    parser.add_argument("--output", default="charts/market-data-dashboard.png", help="PNG output path.")
    parser.add_argument("--symbol", default="BTCUSDT", help="Symbol label for the dashboard.")
    args = parser.parse_args()

    root = Path(args.input)
    output = Path(args.output)
    output.parent.mkdir(parents=True, exist_ok=True)

    trades = read_dataset(root, DATASETS["trades"])
    features = read_dataset(root, DATASETS["features"])
    depth = read_dataset(root, DATASETS["depth"])

    if trades.empty or features.empty:
        raise SystemExit("Expected raw_agg_trades and features Parquet files before plotting.")

    trades = trades.sort_values("trade_ts_ms")
    features = features.sort_values("event_ts_ms")
    features["ts"] = as_utc(features["event_ts_ms"])
    trades["ts"] = as_utc(trades["trade_ts_ms"])
    if not depth.empty:
        depth = depth.sort_values("event_ts_ms")
        depth["ts"] = as_utc(depth["event_ts_ms"])

    ohlc = build_ohlc(trades)
    volume = aggregate_volume(trades)

    avg_spread = features["spread"].mean()
    max_spread = features["spread"].max()
    avg_latency = pd.concat(
        [
            trades.get("receive_latency_ms", pd.Series(dtype=float)),
            depth.get("receive_latency_ms", pd.Series(dtype=float)),
        ],
        ignore_index=True,
    ).mean()

    plt.style.use("dark_background")
    fig, axes = plt.subplots(3, 2, figsize=(18, 14))
    fig.subplots_adjust(left=0.06, right=0.985, bottom=0.065, top=0.875, hspace=0.36, wspace=0.13)
    fig.patch.set_facecolor("#08111f")
    for ax in axes.flat:
        ax.set_facecolor("#0f1b2d")

    fig.suptitle(
        f"{args.symbol} Market Data Research Dashboard",
        fontsize=24,
        fontweight="bold",
        color="#f8fafc",
        y=0.975,
    )
    fig.text(
        0.5,
        0.935,
        f"Rows: trades={len(trades):,}, depth={len(depth):,}, features={len(features):,} | "
        f"avg spread={avg_spread:.4f} USDT, max spread={max_spread:.4f} USDT, avg latency={avg_latency:.3f} ms",
        ha="center",
        color="#b7c4d4",
        fontsize=11,
    )

    ax = axes[0, 0]
    draw_candles(ax, ohlc)
    ax.set_title("OHLC From Aggregate Trades", loc="left", fontweight="bold")
    style_axis(ax, "Price (USDT)")

    ax = axes[0, 1]
    ax.plot(features["ts"], features["midprice"], label="midprice", color="#f8fafc", linewidth=1.8)
    ax.plot(features["ts"], features["best_bid"], label="best bid", color="#2dd4bf", linewidth=1.2)
    ax.plot(features["ts"], features["best_ask"], label="best ask", color="#fb7185", linewidth=1.2)
    ax.set_title("Midprice And L1 Quote", loc="left", fontweight="bold")
    style_axis(ax, "Price (USDT)")
    ax.legend(loc="upper left", frameon=False, fontsize=9)

    ax = axes[1, 0]
    ax.plot(features["ts"], features["spread"], color="#facc15", linewidth=1.8)
    ax.axhline(avg_spread, color="#fef3c7", linestyle="--", linewidth=1, label=f"avg {avg_spread:.4f} USDT")
    ax.set_title("Bid-Ask Spread", loc="left", fontweight="bold")
    style_axis(ax, "Spread (USDT)")
    ax.legend(loc="upper left", frameon=False, fontsize=9)

    ax = axes[1, 1]
    width = 0.000002 if len(volume) < 2 else (mdates.date2num(volume.index[1]) - mdates.date2num(volume.index[0])) * 0.8
    ax.bar(volume.index, volume["buy"], width=width, color="#22c55e", label="buy volume")
    ax.bar(volume.index, volume["sell"], width=width, bottom=volume["buy"], color="#fb7185", label="sell volume")
    ax.set_title("Aggregate Trade Volume", loc="left", fontweight="bold")
    style_axis(ax, "Volume (BTC)")
    ax.legend(loc="upper left", frameon=False, fontsize=9)

    ax = axes[2, 0]
    ax.plot(features["ts"], features["order_flow_imbalance"], color="#7dd3fc", label="book OFI", linewidth=1.5)
    ax.plot(features["ts"], features["trade_imbalance"], color="#fb923c", label="trade imbalance", linewidth=1.5)
    ax.axhline(0, color="#94a3b8", linewidth=1, alpha=0.75)
    ax.set_ylim(-1.05, 1.05)
    ax.set_title("Order-Flow Imbalance", loc="left", fontweight="bold")
    style_axis(ax, "Imbalance Ratio")
    ax.legend(loc="upper left", frameon=False, fontsize=9)

    ax = axes[2, 1]
    latency_series = []
    if "receive_latency_ms" in trades:
        latency_series.append((trades["ts"], trades["receive_latency_ms"], "aggTrade latency", "#2dd4bf"))
    if not depth.empty and "receive_latency_ms" in depth:
        latency_series.append((depth["ts"], depth["receive_latency_ms"], "depth latency", "#93c5fd"))
    for ts, values, label, color in latency_series:
        ax.plot(ts, values, label=label, linewidth=1.4, color=color)
    ax.set_title("WebSocket Receive Latency", loc="left", fontweight="bold")
    style_axis(ax, "Latency (ms)")
    ax.legend(loc="upper left", frameon=False, fontsize=9)

    for ax in axes.flat:
        for label in ax.get_xticklabels():
            label.set_rotation(0)
            label.set_ha("center")

    fig.savefig(output, dpi=180, facecolor=fig.get_facecolor())
    print(f"dashboard={output.resolve()}")
    print(f"avg_spread_usdt={avg_spread:.6f}")
    print(f"max_spread_usdt={max_spread:.6f}")
    print(f"avg_latency_ms={avg_latency:.6f}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
