"""
paddle_engine.py
~~~~~~~~~~~~~~~~
PP-Structure detects semantic layout only. PaddleOCR reads each complete 200-DPI
page once, and its lines are then associated with layout regions. Text outside
all layout regions is retained in synthetic text components.
"""

from __future__ import annotations
from importlib.metadata import PackageNotFoundError, version
import logging
import os
import threading
from typing import Optional
import unicodedata

import cv2
from paddleocr import PPStructure, PaddleOCR
from ocr.parser import parse_structure_result

logger = logging.getLogger("ocr.paddle_engine")


class PaddleEngine:
    """
    Wrapper for English PP-Structure layout and full-page PaddleOCR recognition.

    Parameters
    ----------
    lang : str
        Recognition language. This English-only service defaults to "en".
    table : bool
        Bật nhận dạng bảng (mặc định True).
    ocr : bool
        Enable one full-page recognition pass (default True).
    show_log : bool
        In log nội bộ (mặc định False).
    """

    def __init__(
        self,
        lang: str = "en",
        table: bool = False,
        ocr: bool = True,
        detect_missed: bool = True,
        show_log: bool = False,
    ) -> None:
        # Layout model chỉ hỗ trợ "en"/"ch" -> luôn cố định "en" ở đây.
        # OCR nội bộ của PPStructure bị tắt vì nó không đọc được dấu tiếng Việt.
        self._layout_engine = PPStructure(
            lang="en",
            table=table,
            ocr=False,
            show_log=show_log,
        )
        self._layout_lock = threading.Lock()

        self._ocr_engine: Optional[PaddleOCR] = None
        if ocr:
            self._ocr_engine = PaddleOCR(
                lang=lang,
                use_angle_cls=True,
                show_log=show_log,
            )
            self._log_recognition_model(lang)
        self._ocr_lock = threading.Lock()

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def analyze(self, image_path: str) -> list[dict]:
        """
        Detect semantic regions, recognize the full page once, then map every
        OCR line to at most one region. Layout misses cannot remove OCR text.
        """
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

        with self._layout_lock:
            regions: list[dict] = self._layout_engine(layout_image)
        logger.info("PP-Structure found %d regions", len(regions))

        # Scale bbox về hệ toạ độ gốc nếu đã resize ảnh cho layout
        if scale < 1.0:
            inv_scale = 1.0 / scale
            for region in regions:
                if "bbox" in region:
                    x1, y1, x2, y2 = region["bbox"]
                    region["bbox"] = [
                        x1 * inv_scale, y1 * inv_scale,
                        x2 * inv_scale, y2 * inv_scale,
                    ]

        if self._ocr_engine is not None:
            with self._ocr_lock:
                ocr_result = self._ocr_engine.ocr(image, cls=True)
            lines = self._format_ocr_result(ocr_result, offset=(0, 0))
            regions = self._map_lines_to_regions(lines, regions)

        return regions

    def recognize(self, image_path: str):
        raw = self.analyze(image_path)
        return parse_structure_result(raw)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    @staticmethod
    def _expand_ocr_bbox(
        bbox: list,
        image_width: int,
        image_height: int,
    ) -> tuple[int, int, int, int]:
        """Expand a layout bbox only for OCR, preserving the original bbox."""
        x1, y1, x2, y2 = map(float, bbox)
        region_height = max(0.0, y2 - y1)

        # Vietnamese diacritics need more vertical than horizontal breathing room.
        # Keep the padding bounded so adjacent layout regions are not needlessly
        # pulled into the OCR crop.
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
    def _map_lines_to_regions(cls, lines: list[dict], regions: list[dict]) -> list[dict]:
        """Assign each complete OCR line once, using maximum bbox intersection."""
        for region in regions:
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
            region["res"].sort(key=lambda item: (cls._line_bounds(item)[1], cls._line_bounds(item)[0]))
        for line in uncovered:
            regions.append({"type": "text", "bbox": list(cls._line_bounds(line)), "res": [line]})
        return regions

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
                })
            except (ValueError, TypeError, IndexError) as e:
                logger.warning("Skipping malformed OCR line: %s - %s", line, e)
                continue

        return out

    _PROTECTED_ACRONYMS = (
        "AI", "API", "OCR", "PDF", "CPU", "GPU", "HTTP", "HTTPS", "JSON", "UUID",
    )
    _FUZZY_PROTECTED_IDENTIFIERS = ("CorpMindAI", "OpenAI", "FastAPI")

    @staticmethod
    def _edit_distance_one(left: str, right: str) -> bool:
        # The observed English identifier errors are substitutions among
        # uppercase I, lowercase l, and digit 1. Requiring equal length and
        # that exact confusion set prevents generic corrections (A -> AI,
        # DPI -> API, etc.).
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
