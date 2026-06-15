from __future__ import annotations

from dataclasses import asdict, dataclass, is_dataclass
from datetime import datetime, timezone
from typing import Any


def utc_now() -> datetime:
    return datetime.now(timezone.utc)


@dataclass(frozen=True)
class RawDepthRow:
    event_ts: datetime
    local_ts: datetime
    symbol: str
    first_update_id: int
    final_update_id: int
    previous_final_update_id: int | None
    bids_json: str
    asks_json: str
    latency_ms: float | None


@dataclass(frozen=True)
class RawAggTradeRow:
    event_ts: datetime
    trade_ts: datetime
    local_ts: datetime
    symbol: str
    trade_id: int
    price: float
    quantity: float
    buyer_is_maker: bool
    trade_side: str
    latency_ms: float | None


@dataclass(frozen=True)
class BookChangeRow:
    event_ts: datetime
    local_ts: datetime
    symbol: str
    side: str
    price: float
    previous_size: float
    new_size: float
    delta_size: float
    change_type: str
    final_update_id: int


@dataclass(frozen=True)
class SnapshotRow:
    event_ts: datetime
    local_ts: datetime
    symbol: str
    last_update_id: int
    bids_json: str
    asks_json: str
    depth_synchronized: bool
    resync_count: int


@dataclass(frozen=True)
class FeatureBarRow:
    bar_ts: datetime
    symbol: str
    midprice: float
    spread: float
    best_bid: float
    best_ask: float
    top5_bid_depth: float
    top5_ask_depth: float
    order_flow_imbalance: float
    trade_buy_volume: float
    trade_sell_volume: float
    trade_imbalance: float
    event_count: int


DATASET_COLUMNS: dict[str, list[str]] = {
    "raw_depth": [
        "event_ts",
        "local_ts",
        "symbol",
        "first_update_id",
        "final_update_id",
        "previous_final_update_id",
        "bids_json",
        "asks_json",
        "latency_ms",
    ],
    "raw_agg_trades": [
        "event_ts",
        "trade_ts",
        "local_ts",
        "symbol",
        "trade_id",
        "price",
        "quantity",
        "buyer_is_maker",
        "trade_side",
        "latency_ms",
    ],
    "book_change_events": [
        "event_ts",
        "local_ts",
        "symbol",
        "side",
        "price",
        "previous_size",
        "new_size",
        "delta_size",
        "change_type",
        "final_update_id",
    ],
    "snapshots": [
        "event_ts",
        "local_ts",
        "symbol",
        "last_update_id",
        "bids_json",
        "asks_json",
        "depth_synchronized",
        "resync_count",
    ],
    "features": [
        "bar_ts",
        "symbol",
        "midprice",
        "spread",
        "best_bid",
        "best_ask",
        "top5_bid_depth",
        "top5_ask_depth",
        "order_flow_imbalance",
        "trade_buy_volume",
        "trade_sell_volume",
        "trade_imbalance",
        "event_count",
    ],
}


def row_to_dict(row: Any) -> dict[str, Any]:
    if is_dataclass(row):
        return asdict(row)
    if isinstance(row, dict):
        return row
    raise TypeError(f"Unsupported row type: {type(row)!r}")

