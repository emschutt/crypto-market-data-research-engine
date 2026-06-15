from __future__ import annotations

from pathlib import Path

import matplotlib.dates as mdates
import matplotlib.pyplot as plt
import pandas as pd
from matplotlib.gridspec import GridSpec

from src.storage import read_dataset


def build_research_charts(input_path: Path, charts_path: Path) -> dict[str, str]:
    charts_path.mkdir(parents=True, exist_ok=True)

    trades = read_dataset(input_path, "raw_agg_trades")
    depth = read_dataset(input_path, "raw_depth")
    changes = read_dataset(input_path, "book_change_events")
    features = read_dataset(input_path, "features")

    _prepare_time_columns(trades, "event_ts")
    _prepare_time_columns(depth, "event_ts")
    _prepare_time_columns(changes, "event_ts")
    _prepare_time_columns(features, "bar_ts")

    fig = plt.figure(figsize=(18, 13), dpi=170)
    fig.patch.set_facecolor("#08111f")
    grid = GridSpec(5, 4, figure=fig, height_ratios=[0.72, 1.4, 1.15, 1.15, 1.05], hspace=0.62, wspace=0.28)

    _draw_header(fig, trades, depth, features)

    ax_price = fig.add_subplot(grid[1, :3])
    ax_volume = fig.add_subplot(grid[1, 3])
    ax_spread = fig.add_subplot(grid[2, :2])
    ax_ofi = fig.add_subplot(grid[2, 2:])
    ax_depth = fig.add_subplot(grid[3, :2])
    ax_change = fig.add_subplot(grid[3, 2:])
    ax_latency = fig.add_subplot(grid[4, :2])
    ax_table = fig.add_subplot(grid[4, 2:])

    _style_axis(ax_price, "Midprice And Top Of Book")
    _style_axis(ax_volume, "Agg Trade Volume")
    _style_axis(ax_spread, "Spread")
    _style_axis(ax_ofi, "Order Flow Imbalance")
    _style_axis(ax_depth, "Top-5 Depth")
    _style_axis(ax_change, "Book Change Mix")
    _style_axis(ax_latency, "WebSocket Latency")
    _style_axis(ax_table, "Dataset Health")

    if not features.empty:
        ax_price.plot(features["bar_ts"], features["midprice"], color="#f8f9fa", linewidth=2.2, label="midprice")
        ax_price.fill_between(features["bar_ts"], features["best_bid"], features["best_ask"], color="#20c997", alpha=0.18, label="bid-ask band")
        ax_price.plot(features["bar_ts"], features["best_bid"], color="#51cf66", linewidth=1.1, alpha=0.9, label="best bid")
        ax_price.plot(features["bar_ts"], features["best_ask"], color="#ff8787", linewidth=1.1, alpha=0.9, label="best ask")
        ax_price.legend(loc="upper left", frameon=False, ncol=4, labelcolor="#d8dee9")
        _format_time_axis(ax_price)

        ax_spread.fill_between(features["bar_ts"], features["spread"], color="#ffd43b", alpha=0.32)
        ax_spread.plot(features["bar_ts"], features["spread"], color="#fcc419", linewidth=1.8)
        ax_spread.set_ylabel("USDT", color="#d8dee9")
        _format_time_axis(ax_spread)

        ax_ofi.axhspan(-0.15, 0.15, color="#2f3b52", alpha=0.38)
        ax_ofi.axhline(0, color="#adb5bd", linewidth=0.8, alpha=0.8)
        ax_ofi.plot(features["bar_ts"], features["order_flow_imbalance"], color="#74c0fc", linewidth=1.7, label="book OFI")
        ax_ofi.plot(features["bar_ts"], features["trade_imbalance"], color="#ff922b", linewidth=1.3, alpha=0.9, label="trade imbalance")
        ax_ofi.set_ylim(-1.05, 1.05)
        ax_ofi.legend(loc="upper left", frameon=False, labelcolor="#d8dee9")
        _format_time_axis(ax_ofi)

        ax_depth.plot(features["bar_ts"], features["top5_bid_depth"], color="#69db7c", linewidth=1.8, label="bid depth")
        ax_depth.plot(features["bar_ts"], features["top5_ask_depth"], color="#ffa8a8", linewidth=1.8, label="ask depth")
        ax_depth.fill_between(features["bar_ts"], features["top5_bid_depth"], color="#69db7c", alpha=0.13)
        ax_depth.fill_between(features["bar_ts"], features["top5_ask_depth"], color="#ffa8a8", alpha=0.13)
        ax_depth.legend(loc="upper left", frameon=False, labelcolor="#d8dee9")
        ax_depth.set_ylabel("BTC depth", color="#d8dee9")
        _format_time_axis(ax_depth)
    else:
        _empty(ax_price, "No feature rows found")
        _empty(ax_spread, "No feature rows found")
        _empty(ax_ofi, "No feature rows found")
        _empty(ax_depth, "No feature rows found")

    if not trades.empty:
        volume = (
            trades.assign(bucket=trades["event_ts"].dt.floor("s"))
            .groupby(["bucket", "trade_side"])["quantity"]
            .sum()
            .unstack(fill_value=0)
        )
        for side in ["buy", "sell"]:
            if side not in volume.columns:
                volume[side] = 0.0
        ax_volume.bar(volume.index, volume["buy"], width=0.000008, color="#40c057", alpha=0.9, label="buy")
        ax_volume.bar(volume.index, volume["sell"], bottom=volume["buy"], width=0.000008, color="#fa5252", alpha=0.9, label="sell")
        ax_volume.legend(loc="upper right", frameon=False, labelcolor="#d8dee9")
        ax_volume.set_ylabel("BTC", color="#d8dee9")
        _format_time_axis(ax_volume)
    else:
        _empty(ax_volume, "No trade rows found")

    if not changes.empty:
        mix = changes.groupby(["event_ts", "change_type"])["delta_size"].count().unstack(fill_value=0).resample("1s").sum()
        for column, color in [("limit_add", "#66d9e8"), ("cancel", "#ff6b6b"), ("cancel_or_trade", "#ff6b6b")]:
            if column in mix.columns:
                ax_change.plot(mix.index, mix[column], color=color, linewidth=1.8, label=column)
        ax_change.legend(loc="upper left", frameon=False, labelcolor="#d8dee9")
        ax_change.set_ylabel("events / sec", color="#d8dee9")
        _format_time_axis(ax_change)
    else:
        _empty(ax_change, "No book-change rows found")

    latency_frames = []
    if not trades.empty and "latency_ms" in trades:
        latency_frames.append(trades[["event_ts", "latency_ms"]].assign(source="agg_trade"))
    if not depth.empty and "latency_ms" in depth:
        latency_frames.append(depth[["event_ts", "latency_ms"]].assign(source="depth"))
    if latency_frames:
        latency = pd.concat(latency_frames, ignore_index=True).dropna(subset=["latency_ms"])
        for source, part in latency.groupby("source"):
            color = "#91a7ff" if source == "depth" else "#63e6be"
            ax_latency.plot(part["event_ts"], part["latency_ms"], linewidth=1.5, label=source, color=color)
        ax_latency.legend(loc="upper left", frameon=False, labelcolor="#d8dee9")
        ax_latency.set_ylabel("ms", color="#d8dee9")
        _format_time_axis(ax_latency)
    else:
        _empty(ax_latency, "No latency samples found")

    _draw_dataset_table(ax_table, trades, depth, changes, features)

    dashboard_path = charts_path / "market_microstructure_dashboard.png"
    fig.savefig(dashboard_path, facecolor=fig.get_facecolor(), bbox_inches="tight")
    plt.close(fig)

    return {"chart": str(dashboard_path)}


def _prepare_time_columns(df: pd.DataFrame, column: str) -> None:
    if not df.empty and column in df.columns:
        df[column] = pd.to_datetime(df[column], utc=True)


def _draw_header(fig: plt.Figure, trades: pd.DataFrame, depth: pd.DataFrame, features: pd.DataFrame) -> None:
    symbol = "BTCUSDT"
    if not features.empty and "symbol" in features:
        symbol = str(features["symbol"].iloc[0])
    elif not trades.empty and "symbol" in trades:
        symbol = str(trades["symbol"].iloc[0])

    fig.text(0.035, 0.965, f"{symbol} Market Data Research Dashboard", color="#f8f9fa", fontsize=25, weight="bold")
    fig.text(
        0.035,
        0.935,
        "High-frequency aggregate trades, L5 depth features, order-flow imbalance, and latency diagnostics",
        color="#a6b3c4",
        fontsize=12,
    )

    metrics = [
        ("trades", f"{len(trades):,}"),
        ("depth updates", f"{len(depth):,}"),
        ("feature bars", f"{len(features):,}"),
        ("avg spread", _format_metric(features["spread"].mean() if not features.empty else None, " USDT")),
        ("total volume", _format_metric(trades["quantity"].sum() if not trades.empty else None, " BTC")),
    ]

    x = 0.035
    for label, value in metrics:
        fig.text(x, 0.885, value, color="#f8f9fa", fontsize=17, weight="bold")
        fig.text(x, 0.862, label.upper(), color="#748094", fontsize=8, weight="bold")
        x += 0.135


def _format_metric(value: float | None, suffix: str) -> str:
    if value is None or pd.isna(value):
        return "n/a"
    return f"{value:,.3f}{suffix}"


def _draw_dataset_table(ax: plt.Axes, trades: pd.DataFrame, depth: pd.DataFrame, changes: pd.DataFrame, features: pd.DataFrame) -> None:
    ax.axis("off")
    rows = [
        ("raw_agg_trades", len(trades), "price, quantity, side, latency"),
        ("raw_depth", len(depth), "update ids, bid/ask deltas"),
        ("book_change_events", len(changes), "per-level liquidity changes"),
        ("features", len(features), "spread, midprice, OFI, depth"),
    ]

    table = ax.table(
        cellText=[[name, f"{count:,}", columns] for name, count, columns in rows],
        colLabels=["dataset", "rows", "research columns"],
        loc="center",
        cellLoc="left",
        colLoc="left",
        colWidths=[0.28, 0.15, 0.57],
    )
    table.auto_set_font_size(False)
    table.set_fontsize(9)
    table.scale(1, 1.65)
    for (row, col), cell in table.get_celld().items():
        cell.set_edgecolor("#27354a")
        cell.set_linewidth(0.7)
        if row == 0:
            cell.set_facecolor("#132036")
            cell.set_text_props(color="#f8f9fa", weight="bold")
        else:
            cell.set_facecolor("#0d1728")
            cell.set_text_props(color="#d8dee9")


def _style_axis(ax: plt.Axes, title: str) -> None:
    ax.set_facecolor("#0d1728")
    ax.set_title(title, color="#f8f9fa", fontsize=12, loc="left", pad=10, weight="bold")
    ax.grid(True, color="#243247", alpha=0.6, linewidth=0.7)
    ax.tick_params(colors="#b8c2d1", labelsize=8)
    for spine in ax.spines.values():
        spine.set_color("#27354a")
    ax.yaxis.label.set_color("#d8dee9")
    ax.xaxis.label.set_color("#d8dee9")


def _format_time_axis(ax: plt.Axes) -> None:
    ax.xaxis.set_major_formatter(mdates.DateFormatter("%H:%M:%S"))
    ax.tick_params(axis="x", rotation=0)


def _empty(ax: plt.Axes, message: str) -> None:
    ax.text(0.5, 0.5, message, ha="center", va="center", color="#a6b3c4", transform=ax.transAxes)

