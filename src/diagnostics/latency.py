from __future__ import annotations

from collections import defaultdict, deque
from dataclasses import dataclass
from datetime import datetime, timezone
from statistics import mean


@dataclass(frozen=True)
class LatencySummary:
    source: str
    samples: int
    average_ms: float
    min_ms: float
    max_ms: float


class WebSocketLatencyTracker:
    def __init__(self, max_samples: int = 10_000) -> None:
        self._max_samples = max_samples
        self._samples: dict[str, deque[float]] = defaultdict(lambda: deque(maxlen=max_samples))

    def record(self, source: str, event_ts: datetime, local_ts: datetime | None = None) -> float:
        observed_at = local_ts or datetime.now(timezone.utc)
        if event_ts.tzinfo is None:
            event_ts = event_ts.replace(tzinfo=timezone.utc)
        lag_ms = max((observed_at - event_ts).total_seconds() * 1000.0, 0.0)
        self._samples[source].append(lag_ms)
        return lag_ms

    def summaries(self) -> list[LatencySummary]:
        summaries: list[LatencySummary] = []
        for source, samples in sorted(self._samples.items()):
            if not samples:
                continue
            values = list(samples)
            summaries.append(
                LatencySummary(
                    source=source,
                    samples=len(values),
                    average_ms=round(mean(values), 3),
                    min_ms=round(min(values), 3),
                    max_ms=round(max(values), 3),
                )
            )
        return summaries

    def as_dict(self) -> list[dict[str, float | int | str]]:
        return [summary.__dict__ for summary in self.summaries()]

