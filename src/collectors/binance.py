from __future__ import annotations

import asyncio
import json
import urllib.parse
import urllib.request
from collections import deque
from datetime import datetime, timezone
from typing import Any

import websockets

from src.config import PipelineConfig
from src.diagnostics import WebSocketLatencyTracker
from src.models import BookChangeRow, FeatureBarRow, RawAggTradeRow, RawDepthRow, SnapshotRow
from src.storage import MarketDataStore


class BinanceCombinedCollector:
    def __init__(
        self,
        config: PipelineConfig,
        store: MarketDataStore,
        latency: WebSocketLatencyTracker,
    ) -> None:
        self.config = config
        self.store = store
        self.latency = latency
        self.bids: dict[float, float] = {}
        self.asks: dict[float, float] = {}
        self.last_update_id: int | None = None
        self.resync_count = 0
        self.depth_events = 0
        self.trade_events = 0
        self._changes_since_bar = 0
        self._last_bar_ms = 0
        self._recent_trades: deque[tuple[int, str, float]] = deque(maxlen=500)

    async def run(self) -> dict[str, int]:
        await self._bootstrap_book()
        url = (
            "wss://stream.binance.com:9443/stream?streams="
            f"{self.config.symbol_lower}@depth@100ms/{self.config.symbol_lower}@aggTrade"
        )
        deadline = asyncio.get_running_loop().time() + self.config.capture_duration_seconds

        async with websockets.connect(url, ping_interval=20, ping_timeout=20) as ws:
            while asyncio.get_running_loop().time() < deadline:
                timeout = max(deadline - asyncio.get_running_loop().time(), 0.05)
                try:
                    payload = await asyncio.wait_for(ws.recv(), timeout=timeout)
                except TimeoutError:
                    break
                self._handle_message(payload)

        return self.store.flush()

    async def _bootstrap_book(self) -> None:
        snapshot = await asyncio.to_thread(self._fetch_snapshot)
        self.last_update_id = int(snapshot["lastUpdateId"])
        self.bids = {float(price): float(size) for price, size in snapshot.get("bids", []) if float(size) > 0}
        self.asks = {float(price): float(size) for price, size in snapshot.get("asks", []) if float(size) > 0}
        now = datetime.now(timezone.utc)
        self.store.write_rows(
            "snapshots",
            [
                SnapshotRow(
                    event_ts=now,
                    local_ts=now,
                    symbol=self.config.symbol_upper,
                    last_update_id=self.last_update_id,
                    bids_json=json.dumps(snapshot.get("bids", [])[: self.config.rest_snapshot_limit]),
                    asks_json=json.dumps(snapshot.get("asks", [])[: self.config.rest_snapshot_limit]),
                    depth_synchronized=True,
                    resync_count=self.resync_count,
                )
            ],
        )

    def _fetch_snapshot(self) -> dict[str, Any]:
        query = urllib.parse.urlencode(
            {"symbol": self.config.symbol_upper, "limit": self.config.rest_snapshot_limit}
        )
        with urllib.request.urlopen(f"https://api.binance.com/api/v3/depth?{query}", timeout=10) as response:
            return json.loads(response.read().decode("utf-8"))

    def _handle_message(self, payload: str | bytes) -> None:
        root = json.loads(payload)
        data = root.get("data", root)
        event_type = data.get("e")
        if event_type == "depthUpdate":
            self._handle_depth(data)
        elif event_type == "aggTrade":
            self._handle_agg_trade(data)

    def _handle_depth(self, data: dict[str, Any]) -> None:
        local_ts = datetime.now(timezone.utc)
        event_ts = _from_epoch_ms(int(data.get("E", 0)))
        latency_ms = self.latency.record("binance.depth", event_ts, local_ts)
        first_update_id = int(data["U"])
        final_update_id = int(data["u"])
        previous_final_update_id = self.last_update_id

        self.store.write_rows(
            "raw_depth",
            [
                RawDepthRow(
                    event_ts=event_ts,
                    local_ts=local_ts,
                    symbol=self.config.symbol_upper,
                    first_update_id=first_update_id,
                    final_update_id=final_update_id,
                    previous_final_update_id=previous_final_update_id,
                    bids_json=json.dumps(data.get("b", [])),
                    asks_json=json.dumps(data.get("a", [])),
                    latency_ms=latency_ms,
                )
            ],
        )

        if previous_final_update_id is not None and final_update_id <= previous_final_update_id:
            return

        for side, updates, book in (("bid", data.get("b", []), self.bids), ("ask", data.get("a", []), self.asks)):
            for raw_price, raw_size in updates:
                price = float(raw_price)
                new_size = float(raw_size)
                previous_size = float(book.get(price, 0.0))
                if new_size == 0.0:
                    book.pop(price, None)
                else:
                    book[price] = new_size
                delta = new_size - previous_size
                if delta == 0:
                    continue
                self._changes_since_bar += 1
                self.store.write_rows(
                    "book_change_events",
                    [
                        BookChangeRow(
                            event_ts=event_ts,
                            local_ts=local_ts,
                            symbol=self.config.symbol_upper,
                            side=side,
                            price=price,
                            previous_size=previous_size,
                            new_size=new_size,
                            delta_size=delta,
                            change_type="limit_add" if delta > 0 else "cancel_or_trade",
                            final_update_id=final_update_id,
                        )
                    ],
                )

        self.last_update_id = final_update_id
        self.depth_events += 1
        self._maybe_emit_feature(event_ts)

    def _handle_agg_trade(self, data: dict[str, Any]) -> None:
        local_ts = datetime.now(timezone.utc)
        event_ts = _from_epoch_ms(int(data.get("E", data.get("T", 0))))
        trade_ts = _from_epoch_ms(int(data.get("T", data.get("E", 0))))
        latency_ms = self.latency.record("binance.agg_trade", trade_ts, local_ts)
        buyer_is_maker = bool(data["m"])
        trade_side = "sell" if buyer_is_maker else "buy"
        quantity = float(data["q"])

        self._recent_trades.append((int(trade_ts.timestamp() * 1000), trade_side, quantity))
        self.trade_events += 1
        self.store.write_rows(
            "raw_agg_trades",
            [
                RawAggTradeRow(
                    event_ts=event_ts,
                    trade_ts=trade_ts,
                    local_ts=local_ts,
                    symbol=self.config.symbol_upper,
                    trade_id=int(data["a"]),
                    price=float(data["p"]),
                    quantity=quantity,
                    buyer_is_maker=buyer_is_maker,
                    trade_side=trade_side,
                    latency_ms=latency_ms,
                )
            ],
        )
        self._maybe_emit_feature(event_ts)

    def _maybe_emit_feature(self, event_ts: datetime) -> None:
        event_ms = int(event_ts.timestamp() * 1000)
        if event_ms - self._last_bar_ms < self.config.bar_ms:
            return
        self._last_bar_ms = event_ms
        if not self.bids or not self.asks:
            return

        best_bid = max(self.bids)
        best_ask = min(self.asks)
        top_bids = sorted(self.bids.items(), reverse=True)[:5]
        top_asks = sorted(self.asks.items())[:5]
        bid_depth = sum(size for _, size in top_bids)
        ask_depth = sum(size for _, size in top_asks)
        depth_total = bid_depth + ask_depth

        cutoff_ms = event_ms - 1000
        buy_volume = sum(qty for ts, side, qty in self._recent_trades if ts >= cutoff_ms and side == "buy")
        sell_volume = sum(qty for ts, side, qty in self._recent_trades if ts >= cutoff_ms and side == "sell")
        trade_total = buy_volume + sell_volume

        self.store.write_rows(
            "features",
            [
                FeatureBarRow(
                    bar_ts=event_ts,
                    symbol=self.config.symbol_upper,
                    midprice=(best_bid + best_ask) / 2.0,
                    spread=best_ask - best_bid,
                    best_bid=best_bid,
                    best_ask=best_ask,
                    top5_bid_depth=bid_depth,
                    top5_ask_depth=ask_depth,
                    order_flow_imbalance=(bid_depth - ask_depth) / depth_total if depth_total else 0.0,
                    trade_buy_volume=buy_volume,
                    trade_sell_volume=sell_volume,
                    trade_imbalance=(buy_volume - sell_volume) / trade_total if trade_total else 0.0,
                    event_count=self._changes_since_bar,
                )
            ],
        )
        self._changes_since_bar = 0


async def run_live_capture(
    config: PipelineConfig,
    store: MarketDataStore,
    latency: WebSocketLatencyTracker,
) -> dict[str, int]:
    return await BinanceCombinedCollector(config, store, latency).run()


def _from_epoch_ms(value: int) -> datetime:
    return datetime.fromtimestamp(value / 1000.0, tz=timezone.utc)

