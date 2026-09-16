from __future__ import annotations
from typing import Any
import re
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
                provenance = str(item.get("provenance", item.get("source", "ocr")) or "ocr")
            else:
                # Format cũ: tuple/list ([[bbox]], (text, conf))
                bbox = item[0]
                text = item[1][0]
                confidence = float(item[1][1])
                provenance = "ocr"
        except (IndexError, TypeError, ValueError, KeyError):
            continue

        text = unicodedata.normalize("NFC", text)
        confidences.append(confidence)
        blocks.append(OCRBlock(text=text, confidence=confidence, bbox=bbox, provenance=provenance))

    avg = sum(confidences) / len(confidences) if confidences else 0.0
    return blocks, avg


def _provenance(blocks: list[OCRBlock]) -> str:
    values = {block.provenance for block in blocks if block.provenance}
    if not values:
        return "ocr"
    if len(values) == 1:
        return next(iter(values))
    return "hybrid"

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
            table_rows = region.get("table_rows")
            table_cells = region.get("table_cells")
        else:
            blocks, avg_conf = _parse_ocr_res(res)
            table_html = None
            table_rows = None
            table_cells = None

        if table_rows:
            raw_text = "\n".join(
                str(row.get("text", "")).strip()
                for row in table_rows
                if str(row.get("text", "")).strip()
            )
        else:
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
                extraction_provenance=_provenance(blocks),
                rows=table_rows,
                cells=table_cells,
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


def _component_width(component: LayoutComponent) -> float:
    x1, _, x2, _ = _component_bounds(component)
    return max(0.0, x2 - x1)


def _component_has_text(component: LayoutComponent) -> bool:
    return bool(component.raw_text and component.raw_text.strip())


def _component_sort_key(component: LayoutComponent) -> tuple[float, float, int]:
    x1, y1, _, _ = _component_bounds(component)
    return y1, x1, component.order


def _is_attached_sidebar_title(
    component: LayoutComponent,
    spanning: LayoutComponent,
    page_height: int,
) -> bool:
    if component.component_type == ComponentType.TITLE:
        candidate = True
    else:
        candidate = component.synthetic_region and len(component.raw_text.split()) <= 10
    if not candidate:
        return False

    x1, y1, x2, y2 = _component_bounds(component)
    sx1, sy1, sx2, sy2 = _component_bounds(spanning)
    horizontal_overlap = max(0.0, min(x2, sx2) - max(x1, sx1))
    overlap_ratio = horizontal_overlap / max(1.0, min(x2 - x1, sx2 - sx1))
    vertical_gap = max(0.0, max(sy1 - y2, y1 - sy2))
    return overlap_ratio >= 0.4 and vertical_gap <= max(48.0, page_height * 0.08)


def _find_column_split(
    components: list[LayoutComponent],
    page_width: int,
) -> tuple[list[LayoutComponent], list[LayoutComponent]] | None:
    """Find a large horizontal whitespace gap instead of assuming page midpoint."""
    if len(components) < 2:
        return None

    centers = sorted(
        [
            (
                (_component_bounds(component)[0] + _component_bounds(component)[2]) / 2.0,
                component,
            )
            for component in components
        ],
        key=lambda item: (item[0], item[1].order),
    )
    gaps = [
        (centers[index + 1][0] - centers[index][0], index)
        for index in range(len(centers) - 1)
    ]
    if not gaps:
        return None

    largest_gap, split_index = max(gaps, key=lambda item: item[0])
    if largest_gap < page_width * 0.18:
        return None

    threshold = (centers[split_index][0] + centers[split_index + 1][0]) / 2.0
    left = [component for component in components
            if ((_component_bounds(component)[0] + _component_bounds(component)[2]) / 2.0) <= threshold]
    right = [component for component in components
             if ((_component_bounds(component)[0] + _component_bounds(component)[2]) / 2.0) > threshold]
    if not left or not right:
        return None
    return left, right


def _reading_order(
    components: list[LayoutComponent],
    page_width: int,
    page_height: int | None = None,
) -> list[LayoutComponent]:
    """Order layout segments using geometry, including uneven two-column pages.

    The previous implementation relied on a fixed midpoint and required two
    components in each column. That fails when one column is shorter or wider
    than the other. Here we first isolate margins and leading headings, then
    detect columns from the largest horizontal whitespace gap and keep wide
    sidebar/footnote blocks as their own segments.
    """
    if not components:
        return []

    if page_height is None:
        page_height = max((_component_bounds(component)[3] for component in components), default=page_width)

    headers = [
        component for component in components
        if _component_bounds(component)[3] <= page_height * 0.05
    ]
    footers = [
        component for component in components
        if _component_bounds(component)[1] >= page_height * 0.95
    ]
    header_ids = {id(component) for component in headers}
    footer_ids = {id(component) for component in footers}

    empty = [
        component for component in components
        if not _component_has_text(component) and id(component) not in header_ids and id(component) not in footer_ids
    ]
    body = [
        component for component in components
        if id(component) not in header_ids and id(component) not in footer_ids and id(component) not in {id(item) for item in empty}
    ]
    body_sorted = sorted(body, key=_component_sort_key)

    # A run of leading title components is a spanning heading segment even if
    # the detector's bbox covers only the left half of the page.
    leading_titles: list[LayoutComponent] = []
    seen_body = False
    for component in body_sorted:
        if (
            not seen_body
            and component.component_type == ComponentType.TITLE
            and _component_bounds(component)[1] <= page_height * 0.22
        ):
            leading_titles.append(component)
            continue
        seen_body = True

    leading_ids = {id(component) for component in leading_titles}
    body = [component for component in body if id(component) not in leading_ids]
    bottom_body = [
        component for component in body
        if _component_bounds(component)[1] >= page_height * 0.80
    ]
    bottom_ids = {id(component) for component in bottom_body}
    body = [component for component in body if id(component) not in bottom_ids]

    wide = [
        component for component in body
        if _component_width(component) >= page_width * 0.55
    ]
    wide_ids = {id(component) for component in wide}
    narrow = [component for component in body if id(component) not in wide_ids]

    attached_titles: list[LayoutComponent] = []
    attached_ids: set[int] = set()
    for component in narrow:
        if any(_is_attached_sidebar_title(component, span, page_height) for span in wide):
            attached_titles.append(component)
            attached_ids.add(id(component))
    narrow = [component for component in narrow if id(component) not in attached_ids]

    column_split = _find_column_split(narrow, page_width)
    ordered: list[LayoutComponent] = []
    ordered.extend(sorted(headers, key=_component_sort_key))
    ordered.extend(sorted(leading_titles, key=_component_sort_key))

    if column_split is None:
        # No reliable gap: preserve ordinary top-to-bottom order, including
        # wide paragraphs and scanned text lines.
        ordered.extend(sorted(body, key=_component_sort_key))
    else:
        left, right = column_split
        left_ids = {id(component) for component in left}
        right_ids = {id(component) for component in right}
        first_column_y = min(
            (_component_bounds(component)[1] for component in left + right),
            default=float("inf"),
        )
        spanning_before = [
            component for component in wide
            if _component_bounds(component)[1] < first_column_y
        ]
        spanning_after = [
            component for component in wide
            if id(component) not in {id(item) for item in spanning_before}
        ]
        ordered.extend(sorted(spanning_before, key=_component_sort_key))
        ordered.extend(sorted(left, key=_component_sort_key))
        ordered.extend(sorted(right, key=_component_sort_key))
        ordered.extend(sorted(
            [component for component in body
             if id(component) not in left_ids and id(component) not in right_ids and id(component) not in wide_ids],
            key=_component_sort_key,
        ))
        ordered.extend(sorted(spanning_after + attached_titles, key=_component_sort_key))

    ordered.extend(sorted(bottom_body, key=_component_sort_key))
    ordered.extend(sorted(empty, key=_component_sort_key))
    ordered.extend(sorted(footers, key=_component_sort_key))

    # Defensive completion keeps every source component exactly once if a new
    # layout type does not fit one of the segment classifications above.
    seen_ids: set[int] = set()
    unique: list[LayoutComponent] = []
    for component in ordered:
        if id(component) not in seen_ids:
            seen_ids.add(id(component))
            unique.append(component)
    missing = [component for component in components if id(component) not in seen_ids]
    unique.extend(sorted(missing, key=_component_sort_key))
    return unique


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


def _block_bbox(block: OCRBlock, fallback: list) -> list[float]:
    if block.bbox and isinstance(block.bbox[0], (list, tuple)):
        points = [(float(point[0]), float(point[1])) for point in block.bbox]
        return [
            min(point[0] for point in points),
            min(point[1] for point in points),
            max(point[0] for point in points),
            max(point[1] for point in points),
        ]
    if len(block.bbox) == 4:
        return [float(value) for value in block.bbox]
    return list(fallback)


def _looks_like_dense_figure_heading(text: str) -> bool:
    normalized = text.strip()
    if not normalized:
        return False
    if re.match(r"^(?:appendix|annex|chapter|part)\b", normalized, re.IGNORECASE):
        return True
    return len(normalized.split()) <= 16 and not re.search(r"[.!?]$", normalized)


def _expand_dense_full_page_figures(
    components: list[LayoutComponent],
    page_width: int,
    page_height: int,
) -> list[LayoutComponent]:
    """Replace a dense full-page figure with ordered OCR text components.

    A scanned page is still a visual source, but returning one flattened
    ``figure`` makes heading detection and section assignment impossible. Each
    OCR line becomes a geometry-preserving synthetic component; the first
    heading-shaped line becomes a title. The original figure is removed so its
    text cannot be indexed a second time.
    """
    expanded: list[LayoutComponent] = []
    for component in components:
        x1, y1, x2, y2 = _component_bounds(component)
        is_dense = (
            component.component_type == ComponentType.FIGURE
            and _component_has_text(component)
            and _component_width(component) >= page_width * 0.72
            and (y2 - y1) >= page_height * 0.62
            and len(component.blocks) >= 3
        )
        if not is_dense:
            expanded.append(component)
            continue

        synthesized = 0
        for index, block in enumerate(component.blocks):
            if not block.text or not block.text.strip():
                continue
            is_heading = index == 0 and _looks_like_dense_figure_heading(block.text)
            bbox = _block_bbox(block, component.bbox)
            expanded.append(LayoutComponent(
                component_type=ComponentType.TITLE if is_heading else ComponentType.TEXT,
                bbox=bbox,
                order=component.order + synthesized,
                blocks=[block],
                raw_text=block.text.strip(),
                average_confidence=block.confidence,
                source_layout_type="figure_title" if is_heading else "figure_text",
                synthetic_region=True,
                extraction_provenance=block.provenance,
            ))
            synthesized += 1

    return expanded


def build_structured_page(
    response: StructureResponse,
    document_id: str,
    page_number: int,
    width: int,
    height: int,
    extraction_mode: str = "ocr",
    native_line_count: int = 0,
    accounted_line_count: int = 0,
    source_fidelity: float | None = None,
    fidelity_codes: list[str] | None = None,
) -> StructuredPage:
    """Build V1 metadata from existing OCR output without invoking OCR again."""
    components = _expand_dense_full_page_figures(list(response.components), width, height)
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

    ordered = _reading_order(retained, width, height)
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
                provenance=block.provenance,
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
                extraction_provenance=component.extraction_provenance,
            ),
            rows=component.rows,
            cells=component.cells,
            asset=None,
            caption=caption,
        ))

    return StructuredPage(
        page_number=page_number,
        width=width,
        height=height,
        render_dpi=200,
        components=structured,
        extraction_mode=extraction_mode,
        native_line_count=native_line_count,
        accounted_line_count=accounted_line_count,
        source_fidelity=source_fidelity,
        fidelity_codes=fidelity_codes or [],
    )
