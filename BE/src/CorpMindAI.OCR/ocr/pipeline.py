from __future__ import annotations
import logging

from models.ocr_result import LayoutComponent, StructureResponse
from ocr.paddle_engine import PaddleEngine

logger = logging.getLogger("ocr.pipeline")


class OCRPipeline:

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

        n = len(image_paths)
        if n == 0:
            return []
        logger.info("Processing %d pages sequentially with bounded Paddle batches", n)
        results: list[StructureResponse] = []
        for index, image_path in enumerate(image_paths, 1):
            results.append(self.engine.recognize(image_path))
            logger.info("Page %d/%d completed: %s", index, n, image_path)
        return results

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
