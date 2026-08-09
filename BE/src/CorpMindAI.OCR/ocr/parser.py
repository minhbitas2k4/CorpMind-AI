"""
structure_parser.py
~~~~~~~~~~~~~~~~~~~
Chuyển đổi raw output từ PPStructure thành StructureResponse gồm
danh sách LayoutComponent có ngữ nghĩa (title, text, table, figure …).

Cấu trúc raw do PPStructure trả về:
    [
      {
        "type":  "text" | "title" | "table" | "figure" | ...,
        "bbox":  [x1, y1, x2, y2],
        "res":   <OCR result hoặc HTML string tuỳ type>,
      },
      ...
    ]

Với type != "table":
    res = [ ( [[x1,y1],[x2,y2],[x3,y3],[x4,y4]], ("text", conf) ), ... ]

Với type == "table":
    res = { "html": "<table>...</table>", "cell_bbox": [...] }
"""

from __future__ import annotations
from typing import Any
import unicodedata

from models.ocr_result import (
    ComponentType,
    LayoutComponent,
    OCRBlock,
    StructureResponse,
)

# Map từ chuỗi type của PPStructure → ComponentType enum
_TYPE_MAP: dict[str, ComponentType] = {
    "title":          ComponentType.TITLE,
    "text":           ComponentType.TEXT,
    "table":          ComponentType.TABLE,
    "figure":         ComponentType.FIGURE,
    "figure_caption": ComponentType.FIGURE_CAPTION,
    "reference":      ComponentType.REFERENCE,
    "list":           ComponentType.LIST,
}


def _map_type(raw_type: str) -> ComponentType:
    return _TYPE_MAP.get(raw_type.lower(), ComponentType.UNKNOWN)


def _parse_ocr_res(res: Any) -> tuple[list[OCRBlock], float]:
    """
    Parse phần ``res`` của một region non-table thành danh sách OCRBlock
    và confidence trung bình.

    Hỗ trợ cả 2 format PPStructure có thể trả về:
      - Dạng cũ (tuple):  ( [[x1,y1],...,[x4,y4]], ("text", conf) )
      - Dạng mới (dict):  {"text": "...", "confidence": 0.99,
                            "text_region": [[x1,y1],...,[x4,y4]]}
    """
    blocks: list[OCRBlock] = []
    confidences: list[float] = []

    if not res:
        return blocks, 0.0

    for item in res:
        if item is None:
            continue

        try:
            if isinstance(item, dict):
                # Format mới: dict
                bbox = item.get("text_region") or item.get("bbox") or []
                text = item.get("text", "")
                confidence = float(item.get("confidence", 0.0))
            else:
                # Format cũ: tuple/list ([[bbox]], (text, conf))
                bbox = item[0]
                text = item[1][0]
                confidence = float(item[1][1])
        except (IndexError, TypeError, ValueError, KeyError):
            continue

        text = unicodedata.normalize("NFC", text)
        confidences.append(confidence)
        blocks.append(OCRBlock(text=text, confidence=confidence, bbox=bbox))

    avg = sum(confidences) / len(confidences) if confidences else 0.0
    return blocks, avg

def _parse_table_res(res: Any) -> tuple[list[OCRBlock], float, str | None]:
    """
    Parse phần ``res`` của một region table.
    Trả về: (blocks, avg_conf, html_string)
    """
    if not res:
        return [], 0.0, None

    # Full-page OCR maps the same line dictionaries used by other semantic
    # regions into tables. PP-Structure HTML remains supported when present.
    if isinstance(res, list):
        blocks, avg = _parse_ocr_res(res)
        return blocks, avg, None

    if not isinstance(res, dict):
        return [], 0.0, None

    html: str | None = res.get("html")

    # Lấy OCR blocks từ cell nếu có
    cell_res = res.get("cell_bbox") or res.get("res") or []
    blocks, avg = _parse_ocr_res(cell_res)

    return blocks, avg, html


# ---------------------------------------------------------------------------
# Public API
# ---------------------------------------------------------------------------

def parse_structure_result(raw_regions: list[dict]) -> StructureResponse:
    components: list[LayoutComponent] = []
    all_confidences: list[float] = []

    # Sắp xếp theo thứ tự đọc: trên → dưới, trái → phải.
    # bbox = [x1, y1, x2, y2] -> sort theo y1 trước, x1 sau.
    sorted_regions = sorted(
        raw_regions or [],
        key=lambda r: (r.get("bbox", [0, 0, 0, 0])[1], r.get("bbox", [0, 0, 0, 0])[0]),
    )

    for order, region in enumerate(sorted_regions):
        region_type: str = region.get("type", "unknown")
        bbox: list = region.get("bbox", [])
        res: Any = region.get("res")

        ctype = _map_type(region_type)

        if ctype == ComponentType.TABLE:
            blocks, avg_conf, table_html = _parse_table_res(res)
        else:
            blocks, avg_conf = _parse_ocr_res(res)
            table_html = None

        raw_text = " ".join(b.text for b in blocks)
        if raw_text.strip():
            all_confidences.append(avg_conf)

        components.append(
            LayoutComponent(
                component_type=ctype,
                bbox=bbox,
                order=order,
                blocks=blocks,
                raw_text=raw_text,
                average_confidence=avg_conf,
                table_html=table_html,
            )
        )

    page_avg = (
        sum(all_confidences) / len(all_confidences)
        if all_confidences
        else 0.0
    )

    return StructureResponse(
        page_average_confidence=page_avg,
        components=components,
    )
