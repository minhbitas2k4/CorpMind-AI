from __future__ import annotations

from hashlib import sha256
from pathlib import Path
from typing import Protocol
import re

import cv2
import numpy as np

from models.ocr_result import AssetMetadata


_DEFAULT_ASSET_DIR = Path(__file__).resolve().parent.parent / "reconstruction_assets"
_SAFE_IDENTIFIER = re.compile(r"[^A-Za-z0-9_-]")


def safe_identifier(value: str) -> str:
    """Create a deterministic path segment without accepting user paths."""
    original = str(value)
    sanitized = _SAFE_IDENTIFIER.sub("_", original).strip("_") or "item"
    sanitized = sanitized[:96]
    if sanitized != original:
        sanitized = f"{sanitized}-{sha256(original.encode('utf-8')).hexdigest()[:10]}"
    return sanitized


class AssetStore(Protocol):
    def save_asset(
        self,
        document_id: str,
        page_number: int,
        component_id: str,
        image: np.ndarray,
    ) -> AssetMetadata: ...


class LocalAssetStore:
    def __init__(self, base_dir: str | Path = _DEFAULT_ASSET_DIR) -> None:
        self.base_dir = Path(base_dir).resolve()
        self.base_dir.mkdir(parents=True, exist_ok=True)

    def save_asset(
        self,
        document_id: str,
        page_number: int,
        component_id: str,
        image: np.ndarray,
    ) -> AssetMetadata:
        if image is None or image.size == 0:
            raise ValueError("asset image is empty")
        safe_document = safe_identifier(document_id)
        safe_component = safe_identifier(component_id)
        page = max(1, int(page_number))
        directory = (self.base_dir / f"doc-{safe_document}" / f"page-{page}").resolve()
        path = (directory / f"{safe_component}.png").resolve()
        if self.base_dir != path and self.base_dir not in path.parents:
            raise ValueError("asset path escaped configured storage root")
        directory.mkdir(parents=True, exist_ok=True)
        if not cv2.imwrite(str(path), image):
            raise OSError(f"failed to write asset: {path}")
        height, width = image.shape[:2]
        return AssetMetadata(
            asset_id=f"{component_id}-asset",
            path=str(path),
            width=int(width),
            height=int(height),
        )
