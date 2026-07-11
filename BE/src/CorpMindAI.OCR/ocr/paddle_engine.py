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
from typing import Optional

import cv2
from paddleocr import PPStructure, PaddleOCR
from ocr.parser import parse_structure_result


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
        table: bool = True,
        ocr: bool = True,
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

        regions: list[dict] = self._layout_engine(image)

        if self._ocr_engine is not None:
            for region in regions:
                if region.get("type", "").lower() == "table":
                    continue  # bảng đã có HTML riêng từ layout engine

                x1, y1, x2, y2 = map(int, region.get("bbox", [0, 0, 0, 0]))
                crop = image[y1:y2, x1:x2]
                if crop is None or crop.size == 0:
                    region["res"] = []
                    continue

                ocr_result = self._ocr_engine.ocr(crop, cls=True)
                region["res"] = self._format_ocr_result(ocr_result, offset=(x1, y1))

        # Luôn kiểm tra và bổ sung phần bị layout model bỏ sót,
        # KHÔNG xoá các region table/text mà PP-Structure đã tìm đúng.
        if self._ocr_engine is not None:
            missed_regions = self._detect_missed_areas(image, regions)
            regions.extend(missed_regions)

        return regions

    def recognize(self, image_path: str):
        raw = self.analyze(image_path)
        return parse_structure_result(raw)

    # ------------------------------------------------------------------
    # Helpers
    # ------------------------------------------------------------------

    def _detect_missed_areas(self, image, existing_regions: list[dict]) -> list[dict]:
        """
        OCR toàn trang, rồi chỉ giữ lại các dòng text KHÔNG rơi vào bbox
        của bất kỳ region nào mà PP-Structure đã tìm được (tránh trùng lặp
        với nội dung đã có), sau đó gom các dòng liền kề thành từng "region"
        giả lập kiểu text.
        """
        ocr_result = self._ocr_engine.ocr(image, cls=True)
        all_lines = self._format_ocr_result(ocr_result, offset=(0, 0))
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
            bbox, (text, conf) = line
            bbox_abs = [[float(px) + ox, float(py) + oy] for px, py in bbox]
            out.append({
                "text": text,
                "confidence": float(conf),
                "text_region": bbox_abs,
            })

        return out