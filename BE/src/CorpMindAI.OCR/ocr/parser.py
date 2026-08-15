from __future__ import annotations
from typing import Any
import unicodedata

from models.ocr_result import (
    ComponentType,
    LayoutComponent,
    OCRBlock,
    StructureResponse,
    ComponentMetadata,
    FigureCaption,
    StructuredComponent,
    StructuredLine,
    StructuredPage,
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

    if not res:
        return [], 0.0, None

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
                source_layout_type=region_type,
                synthetic_region=bool(region.get("synthetic_region", False)),
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


_V1_TYPE_MAP = {
    ComponentType.TITLE: "title",
    ComponentType.TEXT: "text",
    ComponentType.LIST: "list",
    ComponentType.TABLE: "table",
    ComponentType.FIGURE: "figure",
    ComponentType.REFERENCE: "text",
    ComponentType.FIGURE_CAPTION: "text",
    ComponentType.UNKNOWN: "unknown",
}


def _clamp(value: float) -> float:
    return min(1.0, max(0.0, value))


def normalize_bbox(bbox: list, page_width: int, page_height: int) -> list:
    """Normalize an xyxy box or point polygon without changing its pixels."""
    if page_width <= 0 or page_height <= 0:
        raise ValueError("page width and height must be positive")
    if bbox and isinstance(bbox[0], (list, tuple)):
        return [[_clamp(float(x) / page_width), _clamp(float(y) / page_height)] for x, y in bbox]
    if len(bbox) != 4:
        return []
    x1, y1, x2, y2 = map(float, bbox)
    return [
        _clamp(x1 / page_width), _clamp(y1 / page_height),
        _clamp(x2 / page_width), _clamp(y2 / page_height),
    ]


def _component_bounds(component: LayoutComponent) -> tuple[float, float, float, float]:
    if len(component.bbox) != 4:
        return 0.0, 0.0, 0.0, 0.0
    x1, y1, x2, y2 = map(float, component.bbox)
    return x1, y1, x2, y2


def _reading_order(components: list[LayoutComponent], page_width: int) -> list[LayoutComponent]:
    """Top-to-bottom order with conservative handling of obvious two columns."""
    ordinary = [c for c in components if (_component_bounds(c)[2] - _component_bounds(c)[0]) < page_width * 0.6]
    midpoint = page_width / 2
    left = [c for c in ordinary if _component_bounds(c)[2] <= midpoint * 1.1]
    right = [c for c in ordinary if _component_bounds(c)[0] >= midpoint * 0.9]
    if len(left) < 2 or len(right) < 2:
        return sorted(components, key=lambda c: (_component_bounds(c)[1], _component_bounds(c)[0]))

    spanning = sorted(
        [c for c in components if c not in ordinary],
        key=lambda c: (_component_bounds(c)[1], _component_bounds(c)[0]),
    )
    ordered: list[LayoutComponent] = []
    remaining = list(ordinary)
    lower_bound = float("-inf")
    for wide in spanning:
        wide_y = _component_bounds(wide)[1]
        segment = [c for c in remaining if lower_bound <= (_component_bounds(c)[1] + _component_bounds(c)[3]) / 2 < wide_y]
        ordered.extend(sorted(segment, key=lambda c: (0 if _component_bounds(c)[0] < midpoint else 1, _component_bounds(c)[1], _component_bounds(c)[0])))
        remaining = [c for c in remaining if c not in segment]
        ordered.append(wide)
        lower_bound = _component_bounds(wide)[3]
    ordered.extend(sorted(remaining, key=lambda c: (0 if _component_bounds(c)[0] < midpoint else 1, _component_bounds(c)[1], _component_bounds(c)[0])))
    return ordered


def _caption_target(caption: LayoutComponent, figures: list[LayoutComponent], page_height: int) -> LayoutComponent | None:
    cx1, cy1, cx2, cy2 = _component_bounds(caption)
    candidates: list[tuple[float, LayoutComponent]] = []
    for figure in figures:
        fx1, fy1, fx2, fy2 = _component_bounds(figure)
        overlap = max(0.0, min(cx2, fx2) - max(cx1, fx1))
        overlap_ratio = overlap / max(1.0, min(cx2 - cx1, fx2 - fx1))
        gap = min(abs(cy1 - fy2), abs(fy1 - cy2))
        if overlap_ratio >= 0.6 and gap <= page_height * 0.03:
            candidates.append((gap, figure))
    candidates.sort(key=lambda item: item[0])
    if not candidates or (len(candidates) > 1 and candidates[0][0] == candidates[1][0]):
        return None
    return candidates[0][1]


def build_structured_page(
    response: StructureResponse,
    document_id: str,
    page_number: int,
    width: int,
    height: int,
) -> StructuredPage:
    """Build V1 metadata from existing OCR output without invoking OCR again."""
    components = list(response.components)
    figures = [c for c in components if c.component_type == ComponentType.FIGURE]
    associated_captions: dict[int, LayoutComponent] = {}
    retained: list[LayoutComponent] = []
    for component in components:
        if component.component_type == ComponentType.FIGURE_CAPTION:
            target = _caption_target(component, figures, height)
            if target is not None and id(target) not in associated_captions:
                associated_captions[id(target)] = component
                continue
        retained.append(component)

    ordered = _reading_order(retained, width)
    structured: list[StructuredComponent] = []
    for reading_order, component in enumerate(ordered):
        component_id = f"doc-{document_id}-p{page_number}-c{reading_order:04d}"
        lines = [
            StructuredLine(
                line_id=f"{component_id}-l{line_order:03d}",
                text=block.text,
                confidence=block.confidence,
                bbox=block.bbox,
                normalized_bbox=normalize_bbox(block.bbox, width, height),
            )
            for line_order, block in enumerate(component.blocks)
        ]
        caption_component = associated_captions.get(id(component))
        caption = None
        if caption_component is not None:
            caption = FigureCaption(
                text=caption_component.raw_text,
                lines=[
                    StructuredLine(
                        line_id=f"{component_id}-l{len(lines) + index:03d}",
                        text=block.text,
                        confidence=block.confidence,
                        bbox=block.bbox,
                        normalized_bbox=normalize_bbox(block.bbox, width, height),
                    )
                    for index, block in enumerate(caption_component.blocks)
                ],
            )

        structured.append(StructuredComponent(
            component_id=component_id,
            document_id=document_id,
            page_number=page_number,
            type=_V1_TYPE_MAP.get(component.component_type, "unknown"),
            bbox=component.bbox,
            normalized_bbox=normalize_bbox(component.bbox, width, height),
            reading_order=reading_order,
            confidence=component.average_confidence,
            text=component.raw_text,
            lines=lines,
            metadata=ComponentMetadata(
                source_layout_type=component.source_layout_type,
                synthetic_region=component.synthetic_region,
            ),
            rows=None,
            cells=None,
            asset=None,
            caption=caption,
        ))

    return StructuredPage(
        page_number=page_number,
        width=width,
        height=height,
        render_dpi=200,
        components=structured,
    )
