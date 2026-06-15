from __future__ import annotations

import json
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Iterable

import pandas as pd

from src.models.market_data import DATASET_COLUMNS, row_to_dict


class MarketDataStore:
    def __init__(
        self,
        root_path: Path | str,
        symbol: str,
        file_format: str = "parquet",
        dataset_type: str = "all",
    ) -> None:
        self.root_path = Path(root_path)
        self.symbol = symbol.upper()
        self.file_format = file_format.lower()
        self.dataset_type = dataset_type
        if self.file_format not in {"parquet", "csv"}:
            raise ValueError("file_format must be 'parquet' or 'csv'")
        self._buffers: dict[str, list[dict]] = defaultdict(list)
        self.root_path.mkdir(parents=True, exist_ok=True)

    def write_rows(self, dataset: str, rows: Iterable[object]) -> None:
        if dataset not in DATASET_COLUMNS:
            raise ValueError(f"Unknown dataset: {dataset}")
        if self.dataset_type not in {"all", dataset}:
            return
        self._buffers[dataset].extend(row_to_dict(row) for row in rows)

    def flush(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        for dataset, rows in list(self._buffers.items()):
            if not rows:
                continue
            counts[dataset] = len(rows)
            self._write_dataset(dataset, rows)
            self._buffers[dataset] = []
        return counts

    def _write_dataset(self, dataset: str, rows: list[dict]) -> None:
        df = pd.DataFrame(rows)
        columns = DATASET_COLUMNS[dataset]
        for column in columns:
            if column not in df.columns:
                df[column] = None
        df = df[columns]

        ts_column = "bar_ts" if dataset == "features" else "event_ts"
        df[ts_column] = pd.to_datetime(df[ts_column], utc=True)

        for hour, part in df.groupby(df[ts_column].dt.floor("h")):
            hour_dt = hour.to_pydatetime()
            partition = self._partition_dir(dataset, hour_dt)
            partition.mkdir(parents=True, exist_ok=True)
            stem = f"part-{datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%S%f')}"

            if self.file_format == "parquet":
                file_path = partition / f"{stem}.parquet"
                part.to_parquet(file_path, index=False)
            else:
                file_path = partition / f"{stem}.csv"
                part.to_csv(file_path, index=False)

            meta_path = partition / f"{stem}.meta.json"
            meta_path.write_text(
                json.dumps(
                    {
                        "dataset": dataset,
                        "symbol": self.symbol,
                        "rows": int(len(part)),
                        "format": self.file_format,
                        "first_timestamp": str(part[ts_column].min()),
                        "last_timestamp": str(part[ts_column].max()),
                    },
                    indent=2,
                ),
                encoding="utf-8",
            )

    def _partition_dir(self, dataset: str, hour: datetime) -> Path:
        hour = hour.astimezone(timezone.utc)
        return (
            self.root_path
            / dataset
            / f"symbol={self.symbol}"
            / f"date_utc={hour:%Y-%m-%d}"
            / f"hour_utc={hour:%H}"
        )


def read_dataset(root_path: Path | str, dataset: str) -> pd.DataFrame:
    root = Path(root_path)
    parquet_files = sorted((root / dataset).rglob("*.parquet"))
    csv_files = sorted((root / dataset).rglob("*.csv"))

    frames: list[pd.DataFrame] = []
    for file_path in parquet_files:
        frames.append(pd.read_parquet(file_path))
    for file_path in csv_files:
        frames.append(pd.read_csv(file_path))

    if not frames:
        return pd.DataFrame(columns=DATASET_COLUMNS.get(dataset, []))

    return pd.concat(frames, ignore_index=True)
