from __future__ import annotations

import asyncio
import json
import math
from datetime import datetime, timedelta, timezone
from random import Random

from src.config import PipelineConfig
from src.diagnostics import WebSocketLatencyTracker
from src.models import BookChangeRow, FeatureBarRow, RawAggTradeRow, RawDepthRow, SnapshotRow
from src.storage import MarketDataStore


async def run_mock_capture(
    config: PipelineConfig,
    store: MarketDataStore,
    latency: WebSocketLatencyTracker,
) -> dict[str, int]:
    rng = Random(42)
    steps = max(30, int(config.capture_duration_seconds * 10))
    start = datetime.now(timezone.utc).replace(microsecond=0)
    base_price = 65_000.0
    last_update_id = 1_000_000

    depth_rows: list[RawDepthRow] = []
    trade_rows: list[RawAggTradeRow] = []
    change_rows: list[BookChangeRow] = []
    feature_rows: list[FeatureBarRow] = []
    snapshot_rows: list[SnapshotRow] = []

    bids = {round(base_price - i * 0.5, 2): 1.0 + i * 0.2 for i in range(1, 12)}
    asks = {round(base_price + i * 0.5, 2): 1.0 + i * 0.2 for i in range(1, 12)}

    snapshot_rows.append(
        SnapshotRow(
            event_ts=start,
            local_ts=start,
            symbol=config.symbol_upper,
            last_update_id=last_update_id,
            bids_json=_levels_json(bids, reverse=True),
            asks_json=_levels_json(asks),
            depth_synchronized=True,
            resync_count=0,
        )
    )

    buy_volume = 0.0
    sell_volume = 0.0
    for i in range(steps):
        event_ts = start + timedelta(milliseconds=i * config.bar_ms)
        local_ts = event_ts + timedelta(milliseconds=4 + (i % 5))
        final_update_id = last_update_id + i + 1
        drift = i * 0.045
        cyclical_move = math.sin(i / 8.0) * 18.0 + math.sin(i / 23.0) * 8.0
        impulse = 22.0 if steps * 0.42 < i < steps * 0.52 else 0.0
        price_shift = drift + cyclical_move + impulse

        side = "bid" if (i + int(abs(math.sin(i / 6.0)) * 10)) % 2 == 0 else "ask"
        book = bids if side == "bid" else asks
        price = round((base_price - 0.5 if side == "bid" else base_price + 0.5) + price_shift, 2)
        previous_size = float(book.get(price, 0.0))
        liquidity_pulse = 0.35 if steps * 0.25 < i < steps * 0.38 else -0.2 if steps * 0.60 < i < steps * 0.72 else 0.0
        new_size = max(previous_size + rng.choice([-0.45, -0.2, 0.25, 0.5, 0.8]) + liquidity_pulse, 0.0)
        if new_size == 0:
            book.pop(price, None)
        else:
            book[price] = new_size

        delta = new_size - previous_size
        change_type = "limit_add" if delta > 0 else "cancel"
        change_rows.append(
            BookChangeRow(
                event_ts=event_ts,
                local_ts=local_ts,
                symbol=config.symbol_upper,
                side=side,
                price=price,
                previous_size=previous_size,
                new_size=new_size,
                delta_size=delta,
                change_type=change_type,
                final_update_id=final_update_id,
            )
        )

        depth_latency = latency.record("mock.depth", event_ts, local_ts)
        depth_rows.append(
            RawDepthRow(
                event_ts=event_ts,
                local_ts=local_ts,
                symbol=config.symbol_upper,
                first_update_id=final_update_id,
                final_update_id=final_update_id,
                previous_final_update_id=final_update_id - 1,
                bids_json=_levels_json(bids, reverse=True),
                asks_json=_levels_json(asks),
                latency_ms=depth_latency,
            )
        )

        trade_price = base_price + price_shift + rng.uniform(-3.5, 3.5)
        volatility_boost = 2.4 if steps * 0.42 < i < steps * 0.55 else 1.0
        qty = round(rng.uniform(0.02, 0.55) * volatility_boost, 6)
        buyer_is_maker = (i % 5 == 0) if i < steps * 0.55 else (i % 3 != 0)
        trade_side = "sell" if buyer_is_maker else "buy"
        if trade_side == "buy":
            buy_volume += qty
        else:
            sell_volume += qty

        trade_latency = latency.record("mock.agg_trade", event_ts, local_ts)
        trade_rows.append(
            RawAggTradeRow(
                event_ts=event_ts,
                trade_ts=event_ts,
                local_ts=local_ts,
                symbol=config.symbol_upper,
                trade_id=900_000 + i,
                price=round(trade_price, 2),
                quantity=qty,
                buyer_is_maker=buyer_is_maker,
                trade_side=trade_side,
                latency_ms=trade_latency,
            )
        )

        best_bid = max(price for price in bids if price < base_price + price_shift + 25)
        best_ask_candidates = [price for price in asks if price > best_bid]
        best_ask = min(best_ask_candidates) if best_ask_candidates else best_bid + 0.5
        bid_depth = sum(size for _, size in sorted(bids.items(), reverse=True)[:5])
        ask_depth = sum(size for _, size in sorted(asks.items())[:5])
        depth_total = bid_depth + ask_depth
        trade_total = buy_volume + sell_volume
        feature_rows.append(
            FeatureBarRow(
                bar_ts=event_ts,
                symbol=config.symbol_upper,
                midprice=round((best_bid + best_ask) / 2.0, 2),
                spread=round(best_ask - best_bid, 2),
                best_bid=round(best_bid, 2),
                best_ask=round(best_ask, 2),
                top5_bid_depth=round(bid_depth, 6),
                top5_ask_depth=round(ask_depth, 6),
                order_flow_imbalance=round((bid_depth - ask_depth) / depth_total, 6) if depth_total else 0.0,
                trade_buy_volume=round(buy_volume, 6),
                trade_sell_volume=round(sell_volume, 6),
                trade_imbalance=round((buy_volume - sell_volume) / trade_total, 6) if trade_total else 0.0,
                event_count=i + 1,
            )
        )

        await asyncio.sleep(0)

    snapshot_rows.append(
        SnapshotRow(
            event_ts=start + timedelta(milliseconds=steps * config.bar_ms),
            local_ts=start + timedelta(milliseconds=steps * config.bar_ms + 5),
            symbol=config.symbol_upper,
            last_update_id=last_update_id + steps,
            bids_json=_levels_json(bids, reverse=True),
            asks_json=_levels_json(asks),
            depth_synchronized=True,
            resync_count=0,
        )
    )

    _write_selected(config.dataset_type, store, "raw_depth", depth_rows)
    _write_selected(config.dataset_type, store, "raw_agg_trades", trade_rows)
    _write_selected(config.dataset_type, store, "book_change_events", change_rows)
    _write_selected(config.dataset_type, store, "features", feature_rows)
    _write_selected(config.dataset_type, store, "snapshots", snapshot_rows)
    return store.flush()


def _write_selected(dataset_type: str, store: MarketDataStore, dataset: str, rows: list[object]) -> None:
    if dataset_type in {"all", dataset}:
        store.write_rows(dataset, rows)


def _levels_json(levels: dict[float, float], reverse: bool = False) -> str:
    ordered = sorted(levels.items(), reverse=reverse)[:10]
    return json.dumps([[round(price, 2), round(size, 6)] for price, size in ordered])
