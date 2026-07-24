# # ocr/pipeline.py
# from ocr.paddle_engine import PaddleEngine


# class OCRPipeline:

#     def __init__(self):
#         self.engine = PaddleEngine()

#     def process_image(self, image_path):
#         # recognize() đã parse sẵn thành StructureResponse, KHÔNG parse lại lần 2
#         response = self.engine.recognize(image_path)
#         return response
from __future__ import annotations
import logging
from concurrent.futures import ThreadPoolExecutor, as_completed

from models.ocr_result import LayoutComponent, StructureResponse
from ocr.paddle_engine import PaddleEngine

logger = logging.getLogger("ocr.pipeline")


class OCRPipeline:
    """
    Nhận engine từ ngoài (dependency injection) thay vì tự tạo mới mỗi lần.
    Điều này cho phép tái sử dụng 1 engine đã load model, tránh việc
    load lại PP-Structure + PaddleOCR (rất tốn thời gian) ở mỗi request API.
    Nếu không truyền engine (dùng độc lập/test), vẫn tự tạo mới như cũ.
    """

    def __init__(self, engine: PaddleEngine | None = None):
        self.engine = engine or PaddleEngine()

    def process_image(self, image_path):
        response = self.engine.recognize(image_path)
        return response

    def process_multiple_images(
        self,
        image_paths: list[str],
        max_workers: int | None = None,
    ) -> list[StructureResponse]:
        """
        Xử lý nhiều trang song song bằng ThreadPoolExecutor.

        PaddleOCR释放GIL trong quá trình inference (C++ backend),
        nên ThreadPoolExecutor cho phép CPU song song trên nhiều cores,
        giảm thời gian tổng từ O(N * T) xuống ~O(T) với N trang.

        Parameters
        ----------
        max_workers : int | None
            Số thread tối đa. Mặc định = số trang (để mỗi trang 1 thread).
            Giới hạn max 6 để tránh过度 memory với PDF nhiều trang.
        """
        n = len(image_paths)
        if n == 0:
            return []
        if n == 1:
            logger.info("Processing single page: %s", image_paths[0])
            return [self.engine.recognize(image_paths[0])]

        workers = max_workers or min(n, 6)
        logger.info("Processing %d pages with %d workers", n, workers)

        results: list[StructureResponse | None] = [None] * n

        with ThreadPoolExecutor(max_workers=workers) as executor:
            future_to_idx = {
                executor.submit(self.engine.recognize, path): i
                for i, path in enumerate(image_paths)
            }
            for future in as_completed(future_to_idx):
                idx = future_to_idx[future]
                try:
                    results[idx] = future.result()
                    logger.info("Page %d/%d completed: %s", idx + 1, n, image_paths[idx])
                except Exception as e:
                    logger.error("Page %d failed: %s - %s", idx + 1, image_paths[idx], e)
                    raise

        return results  # type: ignore[return-value]

    @staticmethod
    def merge_responses(responses: list[StructureResponse]) -> StructureResponse:
        if not responses:
            return StructureResponse(page_average_confidence=0.0, components=[])

        all_components: list[LayoutComponent] = []
        total_confidence = 0.0
        offset = 0

        for resp in responses:
            for comp in resp.components:
                comp.order += offset
                all_components.append(comp)
            total_confidence += resp.page_average_confidence * len(resp.components)
            offset += len(resp.components)

        avg_conf = total_confidence / len(all_components) if all_components else 0.0
        return StructureResponse(
            page_average_confidence=round(avg_conf, 4),
            components=all_components,
        )