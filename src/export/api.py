from __future__ import annotations

from pathlib import Path

from fastapi import FastAPI, HTTPException
from fastapi.responses import FileResponse


DATA_ROOT = Path("data/binance")
app = FastAPI(title="Crypto Market Data Research Export API")


@app.get("/datasets")
def list_datasets() -> dict[str, list[str]]:
    if not DATA_ROOT.exists():
        return {"datasets": []}
    return {"datasets": sorted(path.name for path in DATA_ROOT.iterdir() if path.is_dir())}


@app.get("/files/{dataset}")
def list_files(dataset: str) -> dict[str, list[str]]:
    dataset_root = DATA_ROOT / dataset
    if not dataset_root.exists():
        raise HTTPException(status_code=404, detail="Dataset not found")
    return {"files": sorted(str(path) for path in dataset_root.rglob("*.parquet"))}


@app.get("/download/{dataset}/{file_name}")
def download_file(dataset: str, file_name: str) -> FileResponse:
    matches = [path for path in (DATA_ROOT / dataset).rglob(file_name) if path.is_file()]
    if not matches:
        raise HTTPException(status_code=404, detail="File not found")
    return FileResponse(matches[0])

