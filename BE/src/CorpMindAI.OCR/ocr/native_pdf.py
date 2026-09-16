from __future__ import annotations

"""Native PDF text extraction used by the Phase 1 hybrid OCR pipeline.

The raster OCR path remains available for image-only pages and for the explicit
``raster_only`` rollback mode. Native lines are kept as geometry-bearing records
so the layout engine can assign them to regions without recognizing selectable
text a second time.
"""

from dataclasses import dataclass
from typing import Any

import fitz


EXTRACTOR_VERSION = "native-hybrid-1.0"


@dataclass(frozen=True)
class NativeLine:
    text: str
    bbox: tuple[float, float, float, float]
    block_number: int
    line_number: int
    word_count: int
    line_id: str

    def to_image_line(self, scale_x: float, scale_y: float) -> dict[str, Any]:
        x1, y1, x2, y2 = self.bbox
        points = [
            [x1 * scale_x, y1 * scale_y],
            [x2 * scale_x, y1 * scale_y],
            [x2 * scale_x, y2 * scale_y],
            [x1 * scale_x, y2 * scale_y],
        ]
        return {
            "text": self.text,
            "confidence": 1.0,
            "text_region": points,
            "source": "native",
            "source_line_id": self.line_id,
        }


@dataclass(frozen=True)
class NativePage:
    page_number: int
    width: float
    height: float
    lines: tuple[NativeLine, ...]
    image_regions: tuple[tuple[float, float, float, float], ...]
    extraction_mode: str

    @property
    def image_area_ratio(self) -> float:
        page_area = max(1.0, self.width * self.height)
        image_area = sum(
            max(0.0, x2 - x1) * max(0.0, y2 - y1)
            for x1, y1, x2, y2 in self.image_regions
        )
        return min(1.0, image_area / page_area)

    def for_image(self, image_width: int, image_height: int) -> dict[str, Any]:
        scale_x = image_width / max(1.0, self.width)
        scale_y = image_height / max(1.0, self.height)
        return {
            "page_number": self.page_number,
            "pdf_width": self.width,
            "pdf_height": self.height,
            "image_width": image_width,
            "image_height": image_height,
            "extraction_mode": self.extraction_mode,
            "image_area_ratio": self.image_area_ratio,
            "image_regions": [
                [x1 * scale_x, y1 * scale_y, x2 * scale_x, y2 * scale_y]
                for x1, y1, x2, y2 in self.image_regions
            ],
            "lines": [line.to_image_line(scale_x, scale_y) for line in self.lines],
        }


def _line_records(page: fitz.Page, page_number: int) -> tuple[NativeLine, ...]:
    words = page.get_text("words", sort=False) or []
    grouped: dict[tuple[int, int], list[tuple[float, float, float, float, str, int]]] = {}
    for word in words:
        if len(word) < 8:
            continue
        x1, y1, x2, y2, text, block_number, line_number, word_number = word[:8]
        text = str(text or "").strip()
        if not text:
            continue
        key = (int(block_number), int(line_number))
        grouped.setdefault(key, []).append(
            (float(x1), float(y1), float(x2), float(y2), text, int(word_number))
        )

    records: list[NativeLine] = []
    for (block_number, line_number), line_words in grouped.items():
        line_words.sort(key=lambda item: (item[5], item[0]))
        x1 = min(item[0] for item in line_words)
        y1 = min(item[1] for item in line_words)
        x2 = max(item[2] for item in line_words)
        y2 = max(item[3] for item in line_words)
        text = " ".join(item[4] for item in line_words)
        records.append(
            NativeLine(
                text=text,
                bbox=(x1, y1, x2, y2),
                block_number=block_number,
                line_number=line_number,
                word_count=len(line_words),
                line_id=f"native-doc-p{page_number:04d}-b{block_number:04d}-l{line_number:04d}",
            )
        )

    records.sort(key=lambda line: (line.bbox[1], line.bbox[0], line.block_number, line.line_number))
    return tuple(records)


def _image_regions(page: fitz.Page) -> tuple[tuple[float, float, float, float], ...]:
    data = page.get_text("dict") or {}
    regions: list[tuple[float, float, float, float]] = []
    for block in data.get("blocks", []):
        if block.get("type") != 1:
            continue
        bbox = block.get("bbox") or []
        if len(bbox) != 4:
            continue
        regions.append(tuple(float(value) for value in bbox))
    return tuple(regions)


def _classify(lines: tuple[NativeLine, ...], image_regions: tuple[tuple[float, float, float, float], ...]) -> str:
    if lines and image_regions:
        return "mixed"
    if lines:
        return "born_digital"
    if image_regions:
        return "scanned"
    return "scanned"


def extract_native_pdf_pages(pdf_path: str) -> list[NativePage]:
    """Extract words/lines and classify each PDF page independently."""
    pages: list[NativePage] = []
    with fitz.open(pdf_path) as document:
        for index, page in enumerate(document, 1):
            lines = _line_records(page, index)
            image_regions = _image_regions(page)
            pages.append(
                NativePage(
                    page_number=index,
                    width=float(page.rect.width),
                    height=float(page.rect.height),
                    lines=lines,
                    image_regions=image_regions,
                    extraction_mode=_classify(lines, image_regions),
                )
            )
    return pages
