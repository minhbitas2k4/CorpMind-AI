from __future__ import annotations

import math

import numpy as np

from models.ocr_result import StructuredPage
from reconstruction.asset_store import AssetStore


_ASSET_COMPONENT_TYPES = {"table", "figure", "unknown"}


def preserve_page_assets(
    document_id: str,
    page: StructuredPage,
    page_image: np.ndarray,
    store: AssetStore,
) -> None:
    """Attach durable crops without changing component geometry or failing OCR."""
    for component in page.components:
        if component.type not in _ASSET_COMPONENT_TYPES:
            continue
        original_bbox = list(component.bbox)
        try:
            if page_image is None or page_image.size == 0 or len(component.bbox) != 4:
                raise ValueError("page image or component bbox is invalid")
            image_height, image_width = page_image.shape[:2]
            x1, y1, x2, y2 = map(float, component.bbox)
            crop_x1 = max(0, min(image_width, math.floor(x1)))
            crop_y1 = max(0, min(image_height, math.floor(y1)))
            crop_x2 = max(0, min(image_width, math.ceil(x2)))
            crop_y2 = max(0, min(image_height, math.ceil(y2)))
            if crop_x2 <= crop_x1 or crop_y2 <= crop_y1:
                raise ValueError("component bbox has no meaningful page area")
            crop = page_image[crop_y1:crop_y2, crop_x1:crop_x2]
            component.asset = store.save_asset(
                document_id=document_id,
                page_number=page.page_number,
                component_id=component.component_id,
                image=crop,
            )
        except Exception as error:
            component.asset = None
            component.metadata.asset_errors.append(str(error))
        finally:
            component.bbox = original_bbox
