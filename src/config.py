from __future__ import annotations

import os
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class PipelineConfig:
    symbol: str = "BTCUSDT"
    output_path: Path = Path("data/binance")
    capture_duration_seconds: float = 10.0
    dataset_type: str = "all"
    mode: str = "mock"
    bar_ms: int = 100
    rest_snapshot_limit: int = 1000
    file_format: str = "parquet"

    @property
    def symbol_upper(self) -> str:
        return self.symbol.upper()

    @property
    def symbol_lower(self) -> str:
        return self.symbol.lower()

    @classmethod
    def from_env(cls) -> "PipelineConfig":
        return cls(
            symbol=os.getenv("SYMBOL", "BTCUSDT"),
            output_path=Path(os.getenv("OUTPUT_PATH", "data/binance")),
            capture_duration_seconds=float(os.getenv("CAPTURE_DURATION_SECONDS", "10")),
            dataset_type=os.getenv("DATASET_TYPE", "all"),
            mode=os.getenv("MODE", "mock"),
            bar_ms=int(os.getenv("BAR_MS", "100")),
            rest_snapshot_limit=int(os.getenv("REST_SNAPSHOT_LIMIT", "1000")),
            file_format=os.getenv("FILE_FORMAT", "parquet"),
        )

