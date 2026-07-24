"""
paddle_engine.py
~~~~~~~~~~~~~~~~
OCR engine dùng PP-Structure để phân tích layout, kết hợp PaddleOCR
riêng cho bước đọc chữ tiếng Việt (vì layout model của PP-Structure
chỉ hỗ trợ lang="en"/"ch", không có "vi").

Ngoài ra, engine có cơ chế "bổ sung vùng bị bỏ sót": sau khi PP-Structure
phân tích layout xong, sẽ OCR lại toàn trang một lần nữa và chỉ giữ lại
những dòng text KHÔNG rơi vào bbox của bất kỳ region nào mà PP-Structure
đã tìm được (tránh trùng lặp). Điều này giúp không mất nội dung với các
tài liệu thưa chữ (như hoá đơn, form đơn giản) mà vẫn giữ nguyên các
bảng (table) mà PP-Structure nhận diện đúng.
"""

from __future__ import annotations
import logging
import traceback
from typing import Optional

import cv2
from paddleocr import PPStructure, PaddleOCR
from ocr.parser import parse_structure_result

logger = logging.getLogger("ocr.paddle_engine")


class PaddleEngine:
    """
    Wrapper cho PP-Structure + PaddleOCR (tiếng Việt).

    Parameters
    ----------
    lang : str
        Ngôn ngữ dùng cho bước RECOGNITION (đọc chữ). Mặc định "vi".
    table : bool
        Bật nhận dạng bảng (mặc định True).
    ocr : bool
        Bật đọc chữ tiếng Việt trên từng vùng layout (mặc định True).
    show_log : bool
        In log nội bộ (mặc định False).
    """

    def __init__(
        self,
        lang: str = "vi",
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

        self._ocr_engine: Optional[PaddleOCR] = None
        if ocr:
            self._ocr_engine = PaddleOCR(
                lang=lang,
                use_angle_cls=True,
                show_log=show_log,
            )
        self._detect_missed = detect_missed

    # ------------------------------------------------------------------
    # Public API
    # ------------------------------------------------------------------

    def analyze(self, image_path: str) -> list[dict]:
        """
        Phân tích layout (bằng PP-Structure) rồi đọc chữ tiếng Việt
        (bằng PaddleOCR) trên từng vùng non-table.

        Sau đó, bổ sung thêm các đoạn text mà layout model đã bỏ sót
        (không rơi vào bbox của bất kỳ region nào đã tìm được), để đảm
        bảo không mất nội dung dù tài liệu có bảng hay không.
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
            for region in regions:
                if region.get("type", "").lower() == "table":
                    continue  # bảng đã có HTML riêng từ layout engine

                x1, y1, x2, y2 = map(int, region.get("bbox", [0, 0, 0, 0]))
                x1, y1 = max(0, x1), max(0, y1)
                x2, y2 = min(image.shape[1], x2), min(image.shape[0], y2)
                if x2 - x1 < 5 or y2 - y1 < 5:
                    region["res"] = []
                    continue

                crop = image[y1:y2, x1:x2]
                if crop is None or crop.size == 0:
                    region["res"] = []
                    continue

                try:
                    ocr_result = self._ocr_engine.ocr(crop, cls=True)
                    region["res"] = self._format_ocr_result(ocr_result, offset=(x1, y1))
                except Exception as e:
                    logger.warning("OCR failed on region bbox=%s: %s", [x1, y1, x2, y2], e)
                    region["res"] = []

        # Luôn kiểm tra và bổ sung phần bị layout model bỏ sót,
        # KHÔNG xoá các region table/text mà PP-Structure đã tìm đúng.
        # Tuy nhiên, bỏ qua bước này nếu:
        #   - detect_missed bị tắt, HOẶC
        #   - PP-Structure đã tìm đủ region với confidence cao
        #     (giả định tài liệu đã được cover đầy đủ).
        if self._ocr_engine is not None and self._detect_missed:
            should_detect = self._should_detect_missed(regions)
            if should_detect:
                try:
                    missed_regions = self._detect_missed_areas(image, regions)
                    regions.extend(missed_regions)
                    if missed_regions:
                        logger.info("Found %d missed regions", len(missed_regions))
                except Exception as e:
                    logger.warning("Missed area detection failed: %s", e)
            else:
                logger.info("Skipping missed area detection (regions look complete)")

        return regions

    def recognize(self, image_path: str):
        raw = self.analyze(image_path)
        return parse_structure_result(raw)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _should_detect_missed(self, regions: list[dict]) -> bool:
        """
        Quyết định có cần chạy _detect_missed_areas hay không.

        Logic:
        - Nếu có <= 2 region → có thể tài liệu thưa chữ, nên chạy để bắt text thừa.
        - Nếu tất cả region đều có type rõ ràng (text/table/title/figure) và
          confidence trung bình >= 0.90 → bỏ qua, tiết kiệm thời gian.
        - Ngược lại → chạy để đảm bảo không mất nội dung.
        """
        if len(regions) <= 2:
            return True

        text_regions = [r for r in regions if r.get("type", "").lower() in ("text", "title")]
        if not text_regions:
            return True

        # Tính average confidence từ OCR results trong regions
        total_conf = 0.0
        count = 0
        for r in regions:
            for line in r.get("res", []):
                if isinstance(line, dict) and "confidence" in line:
                    total_conf += line["confidence"]
                    count += 1

        if count == 0:
            return True

        avg_conf = total_conf / count
        # Nếu confidence trung bình cao và có nhiều region → bỏ qua missed areas
        if avg_conf >= 0.90 and len(regions) >= 4:
            return False

        return True

    def _detect_missed_areas(self, image, existing_regions: list[dict]) -> list[dict]:
        """
        OCR toàn trang, rồi chỉ giữ lại các dòng text KHÔNG rơi vào bbox
        của bất kỳ region nào mà PP-Structure đã tìm được (tránh trùng lặp
        với nội dung đã có), sau đó gom các dòng liền kề thành từng "region"
        giả lập kiểu text.
        """
        # Resize về max 1920px để tránh OOM trong Paddle detector
        h, w = image.shape[:2]
        max_dim = 1920
        scale = min(max_dim / w, max_dim / h, 1.0)
        if scale < 1.0:
            new_w, new_h = int(w * scale), int(h * scale)
            image = cv2.resize(image, (new_w, new_h), interpolation=cv2.INTER_AREA)

        ocr_result = self._ocr_engine.ocr(image, cls=True)
        all_lines = self._format_ocr_result(ocr_result, offset=(0, 0))

        # Scale coordinates back if resized
        if scale < 1.0:
            inv_scale = 1.0 / scale
            for line in all_lines:
                line["text_region"] = [[p[0] * inv_scale, p[1] * inv_scale] for p in line["text_region"]]
                line["bbox"] = line["text_region"]
        if not all_lines:
            return []

        def is_covered(line: dict, regions: list[dict]) -> bool:
            cx = sum(p[0] for p in line["text_region"]) / 4
            cy = sum(p[1] for p in line["text_region"]) / 4
            for r in regions:
                bx1, by1, bx2, by2 = r.get("bbox", [0, 0, 0, 0])
                if bx1 <= cx <= bx2 and by1 <= cy <= by2:
                    return True
            return False

        missed = [l for l in all_lines if not is_covered(l, existing_regions)]
        if not missed:
            return []

        # Sắp xếp theo Y rồi gom nhóm các dòng gần nhau (cùng "đoạn")
        missed.sort(key=lambda l: l["text_region"][0][1])

        groups: list[list[dict]] = []
        current_group: list[dict] = [missed[0]]
        gap_threshold = 40  # px, tuỳ chỉnh theo dpi ảnh của bạn

        for line in missed[1:]:
            prev_y = current_group[-1]["text_region"][2][1]  # y dưới của dòng trước
            curr_y = line["text_region"][0][1]                # y trên của dòng hiện tại
            if curr_y - prev_y > gap_threshold:
                groups.append(current_group)
                current_group = [line]
            else:
                current_group.append(line)
        groups.append(current_group)

        result: list[dict] = []
        for group in groups:
            xs = [p[0] for line in group for p in line["text_region"]]
            ys = [p[1] for line in group for p in line["text_region"]]
            result.append({
                "type": "text",
                "bbox": [min(xs), min(ys), max(xs), max(ys)],
                "res": group,
            })

        return result

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
                    "text": text,
                    "confidence": float(conf),
                    "text_region": bbox_abs,
                })
            except (ValueError, TypeError, IndexError) as e:
                logger.warning("Skipping malformed OCR line: %s - %s", line, e)
                continue

        return out