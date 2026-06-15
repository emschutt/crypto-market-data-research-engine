from __future__ import annotations

from pathlib import Path

import matplotlib.pyplot as plt
import pandas as pd

from src.storage import read_dataset


def build_research_charts(input_path: Path, charts_path: Path) -> dict[str, str]:
    charts_path.mkdir(parents=True, exist_ok=True)

    trades = read_dataset(input_path, "raw_agg_trades")
    features = read_dataset(input_path, "features")

    fig, axes = plt.subplots(3, 1, figsize=(11, 9), constrained_layout=True)
    fig.suptitle("BTCUSDT Market Microstructure Summary", fontsize=15)

    if not trades.empty:
        trades["event_ts"] = pd.to_datetime(trades["event_ts"], utc=True)
        trades["bucket"] = trades["event_ts"].dt.floor("s")
        volume = trades.groupby(["bucket", "trade_side"])["quantity"].sum().unstack(fill_value=0)
        volume.plot(kind="bar", stacked=True, ax=axes[0], color=["#2f9e44", "#c92a2a"])
        axes[0].set_title("Aggregate trade volume by second")
        axes[0].set_xlabel("")
        axes[0].set_ylabel("BTC quantity")
        axes[0].tick_params(axis="x", rotation=20)
    else:
        axes[0].text(0.5, 0.5, "No trade data found", ha="center", va="center")

    if not features.empty:
        features["bar_ts"] = pd.to_datetime(features["bar_ts"], utc=True)
        axes[1].plot(features["bar_ts"], features["midprice"], color="#1971c2", label="midprice")
        axes[1].set_title("Midprice")
        axes[1].set_ylabel("USDT")
        axes[1].tick_params(axis="x", rotation=20)

        ax_spread = axes[1].twinx()
        ax_spread.plot(features["bar_ts"], features["spread"], color="#f08c00", alpha=0.7, label="spread")
        ax_spread.set_ylabel("spread")

        axes[2].plot(features["bar_ts"], features["order_flow_imbalance"], color="#5f3dc4", label="OFI")
        axes[2].axhline(0, color="#495057", linewidth=0.8)
        axes[2].set_title("Top-5 order-flow imbalance")
        axes[2].set_ylabel("(bid depth - ask depth) / total")
        axes[2].tick_params(axis="x", rotation=20)
    else:
        axes[1].text(0.5, 0.5, "No feature data found", ha="center", va="center")
        axes[2].text(0.5, 0.5, "No feature data found", ha="center", va="center")

    png_path = charts_path / "market_microstructure_summary.png"
    fig.savefig(png_path, dpi=160)
    plt.close(fig)

    return {"chart": str(png_path)}

