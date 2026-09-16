from __future__ import annotations

"""Omission-aware checks for native PDF lines after OCR/layout assembly."""

import re
from typing import Any


FIDELITY_CODES = (
    "SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD",
    "SOURCE_LINE_UNACCOUNTED",
    "SOURCE_EXTRACTION_DUPLICATE",
)


def _normalize(value: str) -> str:
    return re.sub(r"\s+", " ", (value or "").strip()).casefold()


def _bounds(value: Any) -> tuple[float, float, float, float] | None:
    if not value:
        return None
    if isinstance(value, (list, tuple)) and len(value) == 4 and all(
        isinstance(item, (int, float)) for item in value
    ):
        return tuple(float(item) for item in value)
    if isinstance(value, (list, tuple)) and value and all(
        isinstance(point, (list, tuple)) and len(point) >= 2 for point in value
    ):
        xs = [float(point[0]) for point in value]
        ys = [float(point[1]) for point in value]
        return min(xs), min(ys), max(xs), max(ys)
    return None


def _intersection_over_union(left: tuple[float, float, float, float], right: tuple[float, float, float, float]) -> float:
    x1 = max(left[0], right[0])
    y1 = max(left[1], right[1])
    x2 = min(left[2], right[2])
    y2 = min(left[3], right[3])
    intersection = max(0.0, x2 - x1) * max(0.0, y2 - y1)
    left_area = max(0.0, left[2] - left[0]) * max(0.0, left[3] - left[1])
    right_area = max(0.0, right[2] - right[0]) * max(0.0, right[3] - right[1])
    union = left_area + right_area - intersection
    return intersection / union if union > 0 else 0.0


def _candidate_lines(structured_page: Any) -> list[dict[str, Any]]:
    page = structured_page.model_dump() if hasattr(structured_page, "model_dump") else structured_page
    lines: list[dict[str, Any]] = []
    for component in page.get("components", []):
        for line in component.get("lines", []):
            lines.append(line)
    return lines


def evaluate_page(native_page: dict[str, Any] | None, structured_page: Any) -> dict[str, Any]:
    """Return a deterministic fidelity record for one page.

    Pages without native lines are not evaluable: a scanned page is allowed to
    rely on OCR and must not be failed merely because no native text exists.
    """
    if not native_page:
        return {
            "page_number": None,
            "extraction_mode": "ocr",
            "evaluable": False,
            "native_line_count": 0,
            "accounted_line_count": 0,
            "unaccounted_line_count": 0,
            "duplicate_line_count": 0,
            "fidelity": None,
            "codes": [],
            "unaccounted_line_ids": [],
        }

    source_lines = [line for line in native_page.get("lines", []) if _normalize(line.get("text", ""))]
    candidates = _candidate_lines(structured_page)
    matches_by_source: list[list[dict[str, Any]]] = []
    unaccounted_ids: list[str] = []
    duplicate_count = 0

    for source in source_lines:
        source_text = _normalize(source.get("text", ""))
        source_bounds = _bounds(source.get("text_region"))
        matches = []
        for candidate in candidates:
            if _normalize(candidate.get("text", "")) != source_text:
                continue
            candidate_bounds = _bounds(candidate.get("bbox"))
            if source_bounds is None or candidate_bounds is None:
                matches.append(candidate)
                continue
            if _intersection_over_union(source_bounds, candidate_bounds) >= 0.25:
                matches.append(candidate)

        matches_by_source.append(matches)
        if not matches:
            unaccounted_ids.append(str(source.get("source_line_id", "")))
        elif len(matches) > 1:
            duplicate_count += len(matches) - 1

    total = len(source_lines)
    accounted = total - len(unaccounted_ids)
    fidelity = accounted / total if total else None
    codes: list[str] = []
    if unaccounted_ids:
        codes.append("SOURCE_LINE_UNACCOUNTED")
    if duplicate_count:
        codes.append("SOURCE_EXTRACTION_DUPLICATE")
    if fidelity is not None and fidelity < 1.0:
        codes.append("SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD")

    return {
        "page_number": native_page.get("page_number"),
        "extraction_mode": native_page.get("extraction_mode", "ocr"),
        "evaluable": bool(source_lines),
        "native_line_count": total,
        "accounted_line_count": accounted,
        "unaccounted_line_count": len(unaccounted_ids),
        "duplicate_line_count": duplicate_count,
        "fidelity": fidelity,
        "codes": codes,
        "unaccounted_line_ids": unaccounted_ids,
    }


def aggregate(page_results: list[dict[str, Any]], threshold: float = 1.0) -> dict[str, Any]:
    evaluable = [result for result in page_results if result.get("evaluable")]
    native_line_count = sum(int(result.get("native_line_count", 0)) for result in evaluable)
    accounted_line_count = sum(int(result.get("accounted_line_count", 0)) for result in evaluable)
    unaccounted_line_count = sum(int(result.get("unaccounted_line_count", 0)) for result in evaluable)
    duplicate_line_count = sum(int(result.get("duplicate_line_count", 0)) for result in evaluable)
    fidelity = accounted_line_count / native_line_count if native_line_count else None

    codes: list[str] = []
    if unaccounted_line_count:
        codes.append("SOURCE_LINE_UNACCOUNTED")
    if duplicate_line_count:
        codes.append("SOURCE_EXTRACTION_DUPLICATE")
    if fidelity is not None and fidelity < threshold:
        codes.append("SOURCE_TEXT_FIDELITY_BELOW_THRESHOLD")

    modes: dict[str, int] = {}
    for result in page_results:
        mode = str(result.get("extraction_mode", "ocr"))
        modes[mode] = modes.get(mode, 0) + 1

    return {
        "extractor_version": "native-hybrid-1.0",
        "threshold": threshold,
        "total_pages": len(page_results),
        "evaluable_pages": len(evaluable),
        "page_modes": modes,
        "native_line_count": native_line_count,
        "accounted_line_count": accounted_line_count,
        "unaccounted_line_count": unaccounted_line_count,
        "duplicate_line_count": duplicate_line_count,
        "fidelity": fidelity,
        "codes": sorted(set(codes)),
        "pages": page_results,
    }

