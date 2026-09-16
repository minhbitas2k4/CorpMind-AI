from __future__ import annotations
from importlib.metadata import PackageNotFoundError, version
from html.parser import HTMLParser
import logging
import os
import threading
from typing import Any, Optional
import unicodedata
import re

os.environ.setdefault("OMP_NUM_THREADS", "1")
os.environ.setdefault("MKL_NUM_THREADS", "1")

import cv2
from paddleocr import PPStructure, PaddleOCR
from ocr.parser import parse_structure_result

logger = logging.getLogger("ocr.paddle_engine")


class _TableHtmlParser(HTMLParser):
    """Small dependency-free parser for Paddle table HTML output."""

    def __init__(self) -> None:
        super().__init__(convert_charrefs=True)
        self.rows: list[list[dict[str, Any]]] = []
        self._current_row: list[dict[str, Any]] | None = None
        self._current_cell: dict[str, Any] | None = None

    def handle_starttag(self, tag: str, attrs: list[tuple[str, str | None]]) -> None:
        normalized = tag.casefold()
        if normalized == "tr":
            self._current_row = []
        elif normalized in {"td", "th"} and self._current_row is not None:
            attributes = dict(attrs)
            self._current_cell = {
                "text": [],
                "is_header": normalized == "th",
                "colspan": attributes.get("colspan", "1") or "1",
            }

    def handle_data(self, data: str) -> None:
        if self._current_cell is not None:
            self._current_cell["text"].append(data)

    def handle_endtag(self, tag: str) -> None:
        normalized = tag.casefold()
        if normalized in {"td", "th"} and self._current_cell is not None and self._current_row is not None:
            self._current_row.append({
                "text": " ".join(self._current_cell["text"]),
                "is_header": self._current_cell["is_header"],
                "colspan": self._current_cell["colspan"],
            })
            self._current_cell = None
        elif normalized == "tr" and self._current_row is not None:
            if self._current_row:
                self.rows.append(self._current_row)
            self._current_row = None


class PaddleEngine:
    
    def __init__(
        self,
        lang: str = "en",
        table: bool = False,
        ocr: bool = True,
        detect_missed: bool = True,
        show_log: bool = False,
    ) -> None:

        self._table_enabled = bool(table)
        self._layout_engine = PPStructure(
            lang="en",
            table=False,
            ocr=False,
            cpu_threads=1,
            show_log=show_log,
        )
        self._layout_lock = threading.Lock()
        self._table_layout_engine: Optional[PPStructure] = None
        self._table_layout_lock = threading.Lock()

        self._ocr_language = lang
        self._ocr_show_log = show_log
        self._ocr_enabled = bool(ocr)
        self._ocr_engine: Optional[PaddleOCR] = None
        self._ocr_lock = threading.Lock()

    def _ensure_table_layout_engine(self) -> Optional[PPStructure]:
        """Create the heavyweight table predictor only when a page needs it."""
        if not self._table_enabled:
            return None
        if self._table_layout_engine is not None:
            return self._table_layout_engine
        with self._table_layout_lock:
            if self._table_layout_engine is None:
                self._table_layout_engine = PPStructure(
                    lang="en",
                    table=True,
                    ocr=False,
                    cpu_threads=1,
                    show_log=self._ocr_show_log,
                )
        return self._table_layout_engine

    def _ensure_ocr_engine(self) -> Optional[PaddleOCR]:
        """Create the native OCR predictor once, only when OCR is required."""
        if not self._ocr_enabled:
            return None
        if self._ocr_engine is not None:
            return self._ocr_engine
        with self._ocr_lock:
            if self._ocr_engine is None:
                self._ocr_engine = PaddleOCR(
                    lang=self._ocr_language,
                    use_angle_cls=True,
                    cpu_threads=1,
                    rec_batch_num=1,
                    cls_batch_num=1,
                    show_log=self._ocr_show_log,
                )
                self._log_recognition_model(self._ocr_language)
        return self._ocr_engine


    def analyze(self, image_path: str, native_page: dict | None = None) -> list[dict]:

        image = cv2.imread(image_path)
        if image is None:
            raise FileNotFoundError(f"Không đọc được ảnh: {image_path}")

        h, w = image.shape[:2]
        logger.info("Analyzing image: %s (%dx%d)", image_path, w, h)

        # Resize ảnh xuống max 1920px trước khi chạy PP-Structure
        # để tăng tốc đáng kể và tránh OOM với ảnh lớn.
        # bbox coordinates sẽ được scale về hệ toạ độ gốc bên dưới.
        max_dim = 1920
        scale = min(max_dim / w, max_dim / h, 1.0)
        if scale < 1.0:
            new_w, new_h = int(w * scale), int(h * scale)
            layout_image = cv2.resize(image, (new_w, new_h), interpolation=cv2.INTER_AREA)
            logger.info("Resized for layout analysis: %dx%d (scale=%.2f)", new_w, new_h, scale)
        else:
            layout_image = image

        extraction_mode = (native_page or {}).get("extraction_mode", "ocr")
        with self._layout_lock:
            regions: list[dict] = self._layout_engine(layout_image) or []

        # The lightweight detector still identifies table regions.  Only ask
        # PP-Structure's table-enabled predictor for those regions when the
        # page is not born-digital and therefore cannot use native PDF table
        # geometry.  This keeps scanned-table support while avoiding a global
        # table-model allocation for every document/page.
        has_table_region = any(
            str(region.get("type", "")).casefold() == "table"
            for region in regions
        )
        if self._table_enabled and extraction_mode != "born_digital" and has_table_region:
            table_engine = self._ensure_table_layout_engine()
            if table_engine is not None:
                with self._table_layout_lock:
                    table_regions = table_engine(layout_image) or []
                # Keep the lightweight layout result if the optional table
                # predictor returns no regions.  OCR can still map text to
                # those regions, whereas replacing them with an empty list
                # would discard layout traceability unnecessarily.
                if table_regions:
                    regions = table_regions
        logger.info("PP-Structure found %d regions", len(regions))

        if scale < 1.0:
            inv_scale = 1.0 / scale
            for region in regions:
                if "bbox" in region:
                    x1, y1, x2, y2 = region["bbox"]
                    region["bbox"] = [
                        x1 * inv_scale, y1 * inv_scale,
                        x2 * inv_scale, y2 * inv_scale,
                    ]

        page_number = int((native_page or {}).get("page_number", 0) or 0)
        for region in regions:
            region["page_number"] = page_number

        native_lines = list((native_page or {}).get("lines", []))
        if extraction_mode == "born_digital" and native_lines:
            # Selectable PDF text is authoritative; layout is still used only
            # to assign the native lines to semantic regions.
            regions = self._map_lines_to_regions(native_lines, regions)
        elif extraction_mode == "mixed" and native_lines:
            ocr_engine = self._ensure_ocr_engine()
            if ocr_engine is not None:
                ocr_lines = self._recognize_image_regions(
                    image,
                    (native_page or {}).get("image_regions", []),
                    ocr_engine=ocr_engine,
                )
                lines = self._merge_native_and_ocr_lines(native_lines, ocr_lines)
            else:
                lines = native_lines
            regions = self._map_lines_to_regions(lines, regions)
        elif self._ocr_enabled:
            ocr_engine = self._ensure_ocr_engine()
            if ocr_engine is None:
                return regions
            with self._ocr_lock:
                ocr_result = ocr_engine.ocr(image, cls=True)
            lines = self._format_ocr_result(ocr_result, offset=(0, 0))
            regions = self._map_lines_to_regions(lines, regions)

        return regions

    def recognize(self, image_path: str, native_page: dict | None = None):
        raw = self.analyze(image_path, native_page=native_page)
        return parse_structure_result(raw)


    @staticmethod
    def _expand_ocr_bbox(
        bbox: list,
        image_width: int,
        image_height: int,
    ) -> tuple[int, int, int, int]:
        """Expand a layout bbox only for OCR, preserving the original bbox."""
        x1, y1, x2, y2 = map(float, bbox)
        region_height = max(0.0, y2 - y1)

        vertical_padding = min(max(round(region_height * 0.18), 2), 24)
        horizontal_padding = min(max(round(region_height * 0.08), 2), 16)

        crop_x1 = max(0, int(x1) - horizontal_padding)
        crop_y1 = max(0, int(y1) - vertical_padding)
        crop_x2 = min(image_width, int(x2 + 0.999999) + horizontal_padding)
        crop_y2 = min(image_height, int(y2 + 0.999999) + vertical_padding)
        return crop_x1, crop_y1, crop_x2, crop_y2

    def _log_recognition_model(self, configured_language: str) -> None:
        """Log the model and dictionary resolved by the running PaddleOCR instance."""
        if self._ocr_engine is None:
            return

        args = getattr(self._ocr_engine, "args", None)
        recognizer = getattr(self._ocr_engine, "text_recognizer", None)
        postprocess = getattr(recognizer, "postprocess_op", None)
        characters = set(getattr(postprocess, "character", []) or [])
        required_vietnamese = set(
            "ăâêôơưđáàảãạấầẩẫậếềểễệốồổỗộớờởỡợứừửữựýỳỷỹỵ"
            "ĂÂÊÔƠƯĐÁÀẢÃẠẤẦẨẪẬẾỀỂỄỆỐỒỔỖỘỚỜỞỠỢỨỪỬỮỰÝỲỶỸỴ"
        )
        missing = "".join(sorted(required_vietnamese - characters))

        def package_version(package: str) -> str:
            try:
                return version(package)
            except PackageNotFoundError:
                return "unknown"

        rec_model_dir = getattr(args, "rec_model_dir", None)
        dictionary_path = getattr(args, "rec_char_dict_path", None)
        logger.info(
            "OCR runtime: paddleocr=%s paddlepaddle=%s configured_lang=%s "
            "resolved_lang=%s rec_model=%s rec_model_dir=%s dictionary=%s "
            "dictionary_chars=%d vietnamese_coverage=%s device=%s",
            package_version("paddleocr"),
            package_version("paddlepaddle"),
            configured_language,
            getattr(args, "lang", None),
            os.path.basename(os.path.normpath(rec_model_dir)) if rec_model_dir else None,
            rec_model_dir,
            dictionary_path,
            len(characters),
            "complete" if not missing else f"missing:{missing}",
            "gpu" if getattr(args, "use_gpu", False) else "cpu",
        )

    @staticmethod
    def _line_bounds(line: dict) -> tuple[float, float, float, float]:
        points = line["text_region"]
        xs = [point[0] for point in points]
        ys = [point[1] for point in points]
        return min(xs), min(ys), max(xs), max(ys)

    @classmethod
    def _cluster_table_rows(cls, lines: list[dict]) -> list[list[dict]]:
        rows: list[dict[str, object]] = []
        for line in sorted(lines, key=lambda item: (cls._line_bounds(item)[1], cls._line_bounds(item)[0])):
            _, y1, _, y2 = cls._line_bounds(line)
            center_y = (y1 + y2) / 2.0
            height = max(1.0, y2 - y1)
            matching_row = None
            matching_distance = float("inf")
            for row in rows:
                row_center = float(row["center_y"])
                row_height = float(row["height"])
                overlaps = y1 <= float(row["bottom"]) and y2 >= float(row["top"])
                close_baseline = abs(center_y - row_center) <= max(4.0, min(height, row_height) * 0.65)
                if (overlaps or close_baseline) and abs(center_y - row_center) < matching_distance:
                    matching_row = row
                    matching_distance = abs(center_y - row_center)

            if matching_row is None:
                rows.append({
                    "center_y": center_y,
                    "height": height,
                    "top": y1,
                    "bottom": y2,
                    "lines": [line],
                })
                continue

            row_lines = matching_row["lines"]
            assert isinstance(row_lines, list)
            row_lines.append(line)
            count = len(row_lines)
            matching_row["center_y"] = ((float(matching_row["center_y"]) * (count - 1)) + center_y) / count
            matching_row["height"] = max(float(matching_row["height"]), height)
            matching_row["top"] = min(float(matching_row["top"]), y1)
            matching_row["bottom"] = max(float(matching_row["bottom"]), y2)

        rows.sort(key=lambda row: (float(row["center_y"]), float(row["top"])))
        result: list[list[dict]] = []
        for row in rows:
            row_lines = row["lines"]
            assert isinstance(row_lines, list)
            result.append(sorted(row_lines, key=lambda item: (cls._line_bounds(item)[0], cls._line_bounds(item)[1])))
        return result

    @classmethod
    def _sort_region_lines(cls, lines: list[dict], *, table: bool = False) -> list[dict]:
        """Sort lines deterministically, grouping same-row table fields first.

        PDF word extraction can place a right-column value a fraction of a
        pixel above its left-column label.  A plain ``(y, x)`` sort then emits
        ``value label`` and breaks otherwise complete field/value facts.  For
        table regions, cluster lines whose vertical bands overlap (or whose
        baselines are within one line height) and sort each row left-to-right.
        Non-table regions retain the original top-to-bottom/left-to-right
        ordering.
        """
        if not table or len(lines) < 2:
            return sorted(lines, key=lambda item: (cls._line_bounds(item)[1], cls._line_bounds(item)[0]))

        rows = cls._cluster_table_rows(lines)
        ordered: list[dict] = []
        for row in rows:
            ordered.extend(row)
        return ordered

    @classmethod
    def _build_native_table(
        cls,
        lines: list[dict],
        page_number: int = 0,
    ) -> tuple[list[dict], list[dict]]:
        """Build deterministic row/cell metadata from geometry-bearing lines."""
        row_lines = cls._cluster_table_rows(lines)
        if not row_lines:
            return [], []

        # Some layout models include a legend or table title in the same
        # detected region.  When a row has the maximum number of populated
        # columns, treat the first such row as the header and discard only the
        # preceding preamble.  Two-column key/value tables remain untouched.
        non_empty_counts = [
            sum(1 for line in row if str(line.get("text", "")).strip())
            for row in row_lines
        ]
        maximum_columns = max(non_empty_counts, default=0)
        if maximum_columns >= 3:
            header_index = next(
                (index for index, count in enumerate(non_empty_counts) if count == maximum_columns),
                0,
            )
            row_lines = row_lines[header_index:]

        selected_lines = [line for row in row_lines for line in row]
        all_x = [cls._line_bounds(line)[0] for line in selected_lines]
        table_width = max(cls._line_bounds(line)[2] for line in selected_lines) - min(all_x)
        anchor_tolerance = max(3.0, table_width * 0.01)
        anchors: list[float] = []
        for x in sorted(all_x):
            if not anchors or abs(x - anchors[-1]) > anchor_tolerance:
                anchors.append(x)
            else:
                anchors[-1] = (anchors[-1] + x) / 2.0

        rows: list[dict[str, Any]] = []
        cells: list[dict[str, Any]] = []
        for row_index, row in enumerate(row_lines):
            grouped: dict[int, list[dict]] = {}
            for line in row:
                x1 = cls._line_bounds(line)[0]
                column_index = min(range(len(anchors)), key=lambda index: abs(anchors[index] - x1))
                grouped.setdefault(column_index, []).append(line)

            row_cells: list[dict[str, Any]] = []
            for column_index in range(len(anchors)):
                source_lines = grouped.get(column_index, [])
                if source_lines:
                    bounds = [cls._line_bounds(line) for line in source_lines]
                    bbox = [
                        min(item[0] for item in bounds),
                        min(item[1] for item in bounds),
                        max(item[2] for item in bounds),
                        max(item[3] for item in bounds),
                    ]
                    text = " ".join(str(line.get("text", "")).strip() for line in source_lines).strip()
                    confidence = sum(float(line.get("confidence", 0.0)) for line in source_lines) / len(source_lines)
                    provenance_values = {str(line.get("source", "ocr")) for line in source_lines}
                    provenance = next(iter(provenance_values)) if len(provenance_values) == 1 else "hybrid"
                    source_line_ids = [str(line.get("source_line_id", "")) for line in source_lines if line.get("source_line_id")]
                else:
                    bbox = [0.0, 0.0, 0.0, 0.0]
                    text = ""
                    confidence = 0.0
                    provenance = "native"
                    source_line_ids = []

                cell = {
                    "row_index": row_index,
                    "column_index": column_index,
                    "page_number": page_number,
                    "text": text,
                    "bbox": bbox,
                    "confidence": round(confidence, 6),
                    "is_header": row_index == 0,
                    "provenance": provenance,
                    "source_line_ids": source_line_ids,
                }
                row_cells.append(cell)
                cells.append(cell)

            rows.append({
                "row_index": row_index,
                "page_number": page_number,
                "is_header": row_index == 0,
                "text": " ".join(cell["text"] for cell in row_cells).strip(),
                "cells": row_cells,
            })

        return rows, cells

    @staticmethod
    def _build_html_table(raw_res: Any, page_number: int = 0) -> tuple[list[dict], list[dict]]:
        if not isinstance(raw_res, dict) or not raw_res.get("html"):
            return [], []

        parser = _TableHtmlParser()
        parser.feed(str(raw_res["html"]))
        rows: list[dict] = []
        cells: list[dict] = []
        for row_index, parsed_row in enumerate(parser.rows):
            row_cells: list[dict] = []
            column_index = 0
            for parsed_cell in parsed_row:
                colspan = max(1, int(parsed_cell.get("colspan", 1)))
                cell = {
                    "row_index": row_index,
                    "column_index": column_index,
                    "page_number": page_number,
                    "text": " ".join(str(parsed_cell.get("text", "")).split()),
                    "bbox": [],
                    "confidence": 0.0,
                    "is_header": bool(parsed_cell.get("is_header", False)) or row_index == 0,
                    "provenance": "ocr",
                    "source_line_ids": [],
                    "colspan": colspan,
                }
                row_cells.append(cell)
                cells.append(cell)
                column_index += colspan
            rows.append({
                "row_index": row_index,
                "page_number": page_number,
                "is_header": any(cell["is_header"] for cell in row_cells),
                "text": " ".join(cell["text"] for cell in row_cells).strip(),
                "cells": row_cells,
            })
        return rows, cells

    @classmethod
    def _map_lines_to_regions(cls, lines: list[dict], regions: list[dict]) -> list[dict]:
        """Assign each complete OCR line once, using maximum bbox intersection."""
        for region in regions:
            region["_raw_res"] = region.get("res")
            region["res"] = []

        uncovered: list[dict] = []
        for line in sorted(lines, key=lambda item: (cls._line_bounds(item)[1], cls._line_bounds(item)[0])):
            lx1, ly1, lx2, ly2 = cls._line_bounds(line)
            best_index = None
            best_overlap = 0.0
            for index, region in enumerate(regions):
                rx1, ry1, rx2, ry2 = map(float, region.get("bbox", [0, 0, 0, 0]))
                overlap = max(0.0, min(lx2, rx2) - max(lx1, rx1)) * max(0.0, min(ly2, ry2) - max(ly1, ry1))
                if overlap > best_overlap:
                    best_overlap = overlap
                    best_index = index
            if best_index is None:
                uncovered.append(line)
            else:
                regions[best_index]["res"].append(line)

        for region in regions:
            region["res"] = cls._sort_region_lines(
                region["res"],
                table=str(region.get("type", "")).casefold() == "table",
            )
            if str(region.get("type", "")).casefold() == "table":
                native_lines = [
                    line for line in region["res"]
                    if str(line.get("source", "")).casefold() == "native"
                ]
                if native_lines:
                    # A layout detector can make the table rectangle slightly
                    # too large and include a caption/legend or definitions
                    # immediately above the header.  The table parser must not
                    # discard those native lines: emit them as ordinary text
                    # regions before building the structured rows.
                    row_lines = cls._cluster_table_rows(region["res"])
                    non_empty_counts = [
                        sum(1 for line in row if str(line.get("text", "")).strip())
                        for row in row_lines
                    ]
                    maximum_columns = max(non_empty_counts, default=0)
                    if maximum_columns >= 3:
                        header_index = next(
                            (index for index, count in enumerate(non_empty_counts) if count == maximum_columns),
                            0,
                        )
                        if header_index > 0:
                            preamble_rows = row_lines[:header_index]
                            region["res"] = [
                                line
                                for row in row_lines[header_index:]
                                for line in row
                            ]
                            for row in preamble_rows:
                                bounds = [cls._line_bounds(line) for line in row]
                                regions.append({
                                    "type": "text",
                                    "bbox": [
                                        min(item[0] for item in bounds),
                                        min(item[1] for item in bounds),
                                        max(item[2] for item in bounds),
                                        max(item[3] for item in bounds),
                                    ],
                                    "res": row,
                                    "synthetic_region": True,
                                })
                    region["table_rows"], region["table_cells"] = cls._build_native_table(
                        region["res"],
                        page_number=int(region.get("page_number", 0) or 0),
                    )
                else:
                    region["table_rows"], region["table_cells"] = cls._build_html_table(
                        region.get("_raw_res"),
                        page_number=int(region.get("page_number", 0) or 0),
                    )
        for line in uncovered:
            regions.append({
                "type": "text",
                "bbox": list(cls._line_bounds(line)),
                "res": [line],
                "synthetic_region": True,
            })
        return regions

    @staticmethod
    def _normalized_line_text(value: str) -> str:
        return re.sub(r"\s+", " ", (value or "").strip()).casefold()

    @classmethod
    def _merge_native_and_ocr_lines(
        cls,
        native_lines: list[dict],
        ocr_lines: list[dict],
    ) -> list[dict]:
        """Keep native lines authoritative and drop OCR duplicates by geometry."""
        merged = list(native_lines)
        for ocr_line in ocr_lines:
            ocr_text = cls._normalized_line_text(ocr_line.get("text", ""))
            ox1, oy1, ox2, oy2 = cls._line_bounds(ocr_line)
            duplicate = False
            for native_line in native_lines:
                native_text = cls._normalized_line_text(native_line.get("text", ""))
                if not native_text or native_text != ocr_text:
                    continue
                nx1, ny1, nx2, ny2 = cls._line_bounds(native_line)
                intersection = max(0.0, min(ox2, nx2) - max(ox1, nx1)) * max(0.0, min(oy2, ny2) - max(oy1, ny1))
                union = max(1.0, (ox2 - ox1) * (oy2 - oy1) + (nx2 - nx1) * (ny2 - ny1) - intersection)
                if intersection / union >= 0.25:
                    duplicate = True
                    break
            if not duplicate:
                ocr_line = dict(ocr_line)
                ocr_line["source"] = "ocr"
                merged.append(ocr_line)

        return sorted(
            merged,
            key=lambda item: (cls._line_bounds(item)[1], cls._line_bounds(item)[0]),
        )

    @staticmethod
    def _format_ocr_result(ocr_result, offset: tuple[int, int]) -> list[dict]:
        """
        Chuyển kết quả ``PaddleOCR.ocr()`` (toạ độ tính theo ảnh crop)
        thành list[dict] {"text", "confidence", "text_region"} với
        toạ độ đã cộng offset để quy về hệ toạ độ ảnh gốc.
        """
        ox, oy = offset
        out: list[dict] = []

        if not ocr_result or not ocr_result[0]:
            return out

        for line in ocr_result[0]:
            if line is None:
                continue
            try:
                bbox, (text, conf) = line
                bbox_abs = [[float(px) + ox, float(py) + oy] for px, py in bbox]
                out.append({
                    "text": PaddleEngine._correct_protected_terms(unicodedata.normalize("NFC", text)),
                    "confidence": float(conf),
                    "text_region": bbox_abs,
                    "source": "ocr",
                })
            except (ValueError, TypeError, IndexError) as e:
                logger.warning("Skipping malformed OCR line: %s - %s", line, e)
                continue

        return out

    def _recognize_image_regions(
        self,
        image,
        regions: list[list[float]],
        ocr_engine: Optional[PaddleOCR] = None,
    ) -> list[dict]:
        """Run OCR only over image regions on a mixed page."""
        ocr_engine = ocr_engine or self._ensure_ocr_engine()
        if ocr_engine is None:
            return []
        if not regions:
            with self._ocr_lock:
                return self._format_ocr_result(ocr_engine.ocr(image, cls=True), offset=(0, 0))

        height, width = image.shape[:2]
        lines: list[dict] = []
        with self._ocr_lock:
            for bbox in regions:
                if len(bbox) != 4:
                    continue
                x1, y1, x2, y2 = map(float, bbox)
                crop_x1, crop_y1, crop_x2, crop_y2 = self._expand_ocr_bbox(
                    [x1, y1, x2, y2], width, height
                )
                if crop_x2 <= crop_x1 or crop_y2 <= crop_y1:
                    continue
                crop = image[crop_y1:crop_y2, crop_x1:crop_x2]
                if crop.size == 0:
                    continue
                result = ocr_engine.ocr(crop, cls=True)
                lines.extend(self._format_ocr_result(result, offset=(crop_x1, crop_y1)))
        return lines

    _PROTECTED_ACRONYMS = (
        "AI", "API", "OCR", "PDF", "CPU", "GPU", "HTTP", "HTTPS", "JSON", "UUID",
    )
    _FUZZY_PROTECTED_IDENTIFIERS = ("CorpMindAI", "OpenAI", "FastAPI")

    @staticmethod
    def _edit_distance_one(left: str, right: str) -> bool:
      
        if len(left) != len(right):
            return False
        differences = [(left_char, right_char) for left_char, right_char in zip(left, right) if left_char != right_char]
        return len(differences) == 1 and set(differences[0]) <= {"I", "l", "1"}

    @classmethod
    def _correct_protected_terms(cls, text: str) -> str:
        """Normalize exact acronym casing and only fuzzy-correct compound identifiers."""
        import re

        def correct(match):
            token = match.group(0)
            exact_acronyms = [term for term in cls._PROTECTED_ACRONYMS if token.casefold() == term.casefold()]
            if len(exact_acronyms) == 1:
                return exact_acronyms[0]

            candidates = [
                term for term in cls._FUZZY_PROTECTED_IDENTIFIERS
                if cls._edit_distance_one(token, term)
            ]
            return candidates[0] if len(candidates) == 1 else token

        return re.sub(r"[A-Za-z0-9]+", correct, text)
