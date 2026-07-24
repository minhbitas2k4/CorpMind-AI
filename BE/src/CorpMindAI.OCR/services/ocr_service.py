import os
import logging
import tempfile

from ocr.pipeline import OCRPipeline
from preprocessing.pdf_to_image import convert_pdf_to_images
from validators.confidence import evaluate, evaluate_components
from validators.rules import validate
from cache.ocr_cache import OCRResultCache

logger = logging.getLogger("ocr.service")


PDF_EXTENSIONS = {".pdf"}


class OCRServiceError(Exception):
    """Lỗi nghiệp vụ khi xử lý OCR, kèm status_code để endpoint map ra HTTP status phù hợp."""

    def __init__(self, message: str, status_code: int = 500):
        self.message = message
        self.status_code = status_code
        super().__init__(message)


class OCRService:
    def __init__(self, pipeline: OCRPipeline):
        self.pipeline = pipeline
        self._cache = OCRResultCache()

    def process(self, file_path: str) -> dict:
        if not os.path.exists(file_path):
            raise OCRServiceError(f"File không tồn tại: {file_path}", status_code=404)

        # Kiểm tra cache trước
        cached = self._cache.get(file_path)
        if cached is not None:
            logger.info("Returning cached result for: %s", file_path)
            return cached

        ext = os.path.splitext(file_path)[1].lower()
        logger.info("Processing file: %s (ext=%s)", file_path, ext)

        if ext in PDF_EXTENSIONS:
            with tempfile.TemporaryDirectory() as tmpdir:
                image_paths = convert_pdf_to_images(file_path, output_dir=tmpdir)
                if not image_paths:
                    raise OCRServiceError("Không thể chuyển đổi PDF sang ảnh.", status_code=400)
                logger.info("PDF converted to %d images", len(image_paths))
                merged = self.pipeline.process_multiple_images(image_paths)
                response = self.pipeline.merge_responses(merged)
                total_pages = len(merged)
        else:
            try:
                response = self.pipeline.process_image(file_path)
            except FileNotFoundError as e:
                raise OCRServiceError(str(e), status_code=404)
            total_pages = 1

        logger.info("OCR completed: %d pages, avg_confidence=%.4f", total_pages, response.page_average_confidence)
        result = {
            "total_pages": total_pages,
            "page_average_confidence": response.page_average_confidence,
            "overall_level": evaluate(response),
            "components": evaluate_components(response),
            "validation_errors": validate(response),
        }

        # Lưu vào cache
        self._cache.put(file_path, result)

        return result