from __future__ import annotations

from pathlib import Path

import pandas as pd
import plotly.express as px
import streamlit as st

from src.storage import read_dataset


st.set_page_config(page_title="Crypto Market Data Research", layout="wide")
st.title("Crypto Market Data Research Dashboard")

data_root = Path(st.sidebar.text_input("Dataset root", "sample_data/smoke"))
symbol = st.sidebar.text_input("Symbol", "BTCUSDT")

trades = read_dataset(data_root, "raw_agg_trades")
features = read_dataset(data_root, "features")

if not trades.empty:
    trades = trades[trades["symbol"] == symbol.upper()].copy()
    trades["event_ts"] = pd.to_datetime(trades["event_ts"], utc=True)
if not features.empty:
    features = features[features["symbol"] == symbol.upper()].copy()
    features["bar_ts"] = pd.to_datetime(features["bar_ts"], utc=True)

left, right, third = st.columns(3)
left.metric("Trades", f"{len(trades):,}")
right.metric("Feature bars", f"{len(features):,}")
third.metric("Datasets", len([df for df in [trades, features] if not df.empty]))

if trades.empty and features.empty:
    st.info("No data found. Run the smoke test or collector first.")
    st.stop()

if not trades.empty:
    volume = (
        trades.assign(bucket=trades["event_ts"].dt.floor("s"))
        .groupby(["bucket", "trade_side"], as_index=False)["quantity"]
        .sum()
    )
    st.plotly_chart(
        px.bar(volume, x="bucket", y="quantity", color="trade_side", title="Trade volume over time"),
        use_container_width=True,
    )

if not features.empty:
    st.plotly_chart(
        px.line(features, x="bar_ts", y=["midprice", "best_bid", "best_ask"], title="Midprice and top of book"),
        use_container_width=True,
    )
    st.plotly_chart(
        px.line(features, x="bar_ts", y=["spread", "order_flow_imbalance", "trade_imbalance"], title="Spread and flow metrics"),
        use_container_width=True,
    )
    st.dataframe(features.tail(200), use_container_width=True)

