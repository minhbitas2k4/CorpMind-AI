import os
import logging
import tempfile
import time
import cv2

from ocr.pipeline import OCRPipeline
from ocr.native_pdf import EXTRACTOR_VERSION, extract_native_pdf_pages
from ocr.source_fidelity import aggregate as aggregate_source_fidelity, evaluate_page
from preprocessing.pdf_to_image import convert_pdf_to_images
from validators.confidence import evaluate, evaluate_components
from validators.rules import validate
from cache.ocr_cache import OCRResultCache
from models.ocr_result import StructuredDocument
from ocr.parser import build_structured_page
from reconstruction.asset_preserver import preserve_page_assets
from reconstruction.asset_store import AssetStore, LocalAssetStore
from reconstruction.artifact_registry import ReconstructionArtifactRegistry
from reconstruction.reconstruction_service import ReconstructionService
from runtime_observability import process_memory_mb

logger = logging.getLogger("ocr.service")


PDF_EXTENSIONS = {".pdf"}
STRUCTURED_SCHEMA_VERSION = "1.1"


class OCRServiceError(Exception):
    """Lỗi nghiệp vụ khi xử lý OCR, kèm status_code để endpoint map ra HTTP status phù hợp."""

    def __init__(self, message: str, status_code: int = 500):
        self.message = message
        self.status_code = status_code
        super().__init__(message)


class OCRService:
    def __init__(
        self,
        pipeline: OCRPipeline,
        asset_store: AssetStore | None = None,
        reconstruction_service: ReconstructionService | None = None,
        artifact_registry: ReconstructionArtifactRegistry | None = None,
        render_dpi: int = 200,
        extraction_mode: str = "native_hybrid",
        source_fidelity_threshold: float = 1.0,
    ):
        self.pipeline = pipeline
        self._cache = OCRResultCache()
        self._asset_store = asset_store or LocalAssetStore()
        self._reconstruction_service = reconstruction_service or ReconstructionService()
        self._artifact_registry = artifact_registry or ReconstructionArtifactRegistry(
            self._reconstruction_service.output_dir
        )
        self._render_dpi = render_dpi
        self._extraction_mode = extraction_mode
        self._source_fidelity_threshold = source_fidelity_threshold

    @property
    def artifact_registry(self) -> ReconstructionArtifactRegistry:
        return self._artifact_registry

    @property
    def extraction_identity(self) -> dict[str, str]:
        return {
            "extractor_version": EXTRACTOR_VERSION,
            "schema_version": STRUCTURED_SCHEMA_VERSION,
            "extraction_mode": self._extraction_mode,
            "render_dpi": str(self._render_dpi),
        }

    def _cache_get(self, file_path: str, identity: dict[str, str]):
        try:
            return self._cache.get(file_path, identity)
        except TypeError:
            # Keep small test/downgrade cache adapters source-compatible.
            return self._cache.get(file_path)

    def _cache_put(self, file_path: str, result: dict, identity: dict[str, str]) -> None:
        try:
            self._cache.put(file_path, result, identity)
        except TypeError:
            self._cache.put(file_path, result)

    def _process_multiple_images(self, image_paths: list[str], native_pages: list[dict] | None):
        try:
            return self.pipeline.process_multiple_images(image_paths, native_pages=native_pages)
        except TypeError:
            return self.pipeline.process_multiple_images(image_paths)

    def process(self, file_path: str, document_id: str = "") -> dict:
        process_started = time.perf_counter()
        memory_before = process_memory_mb()
        logger.info(
            "OCR document started: document_id=%s working_set_mb=%s private_mb=%s",
            document_id, memory_before[0], memory_before[1],
        )
        if not os.path.exists(file_path):
            raise OCRServiceError("Input file does not exist", status_code=404)

        # Kiểm tra cache trước
        cache_identity = self.extraction_identity
        # Cache identity includes extraction mode, schema, extractor, and DPI;
        # old raster-only/schema-1.0 entries therefore cannot be reused.
        cached = self._cache_get(file_path, cache_identity)
        cached_artifact = cached.get("reconstruction_artifact") if cached is not None else None
        cached_artifact_id = cached_artifact.get("artifact_id") if cached_artifact else None
        cached_reconstruction_complete = cached is not None and "reconstruction_artifact" in cached
        cached_artifact_available = (
            cached_artifact_id
            and self._artifact_registry.get(document_id, cached_artifact_id) is not None
        )
        if (
            cached is not None
            and cached.get("schema_version") == STRUCTURED_SCHEMA_VERSION
            and cached.get("document_id") == document_id
            and cached.get("extraction_identity") == cache_identity
            and cached_reconstruction_complete
            and (cached_artifact is None or cached_artifact_available)
        ):
            logger.info("Returning cached OCR result: document_id=%s", document_id)
            return cached

        ext = os.path.splitext(file_path)[1].lower()
        logger.debug("Processing OCR input: document_id=%s extension=%s", document_id, ext)

        if ext in PDF_EXTENSIONS:
            asset_processing_seconds = 0.0
            native_pages = (
                extract_native_pdf_pages(file_path)
                if self._extraction_mode == "native_hybrid"
                else []
            )
            with tempfile.TemporaryDirectory() as tmpdir:
                image_paths = convert_pdf_to_images(
                    file_path, output_dir=tmpdir, dpi=self._render_dpi
                )
                if not image_paths:
                    raise OCRServiceError("Không thể chuyển đổi PDF sang ảnh.", status_code=400)
                logger.info("PDF converted to %d images", len(image_paths))
                native_page_inputs: list[dict] = []
                if native_pages:
                    for page_index, (image_path, native_page) in enumerate(zip(image_paths, native_pages), 1):
                        image = cv2.imread(image_path)
                        if image is None:
                            raise OCRServiceError(f"Cannot read rendered page {page_index}")
                        native_page_inputs.append(native_page.for_image(image.shape[1], image.shape[0]))
                page_responses = self._process_multiple_images(
                    image_paths,
                    native_page_inputs if native_page_inputs else None,
                )
                structured_pages = []
                page_fidelity_results = []
                for page_number, (image_path, page_response) in enumerate(zip(image_paths, page_responses), 1):
                    image = cv2.imread(image_path)
                    if image is None:
                        raise OCRServiceError(f"Cannot read rendered page {page_number}")
                    height, width = image.shape[:2]
                    native_page_input = (
                        native_page_inputs[page_number - 1]
                        if page_number <= len(native_page_inputs)
                        else None
                    )
                    preliminary_page = build_structured_page(
                        page_response,
                        document_id,
                        page_number,
                        width,
                        height,
                        extraction_mode=(native_page_input or {}).get("extraction_mode", "ocr"),
                    )
                    fidelity = evaluate_page(native_page_input, preliminary_page)
                    structured_page = build_structured_page(
                        page_response,
                        document_id,
                        page_number,
                        width,
                        height,
                        extraction_mode=(native_page_input or {}).get("extraction_mode", "ocr"),
                        native_line_count=fidelity["native_line_count"],
                        accounted_line_count=fidelity["accounted_line_count"],
                        source_fidelity=fidelity["fidelity"],
                        fidelity_codes=fidelity["codes"],
                    )
                    asset_started = time.perf_counter()
                    preserve_page_assets(document_id, structured_page, image, self._asset_store)
                    asset_processing_seconds += time.perf_counter() - asset_started
                    structured_pages.append(structured_page)
                    page_fidelity_results.append(fidelity)
                response = self.pipeline.merge_responses(page_responses)
                total_pages = len(page_responses)
            source_fidelity = aggregate_source_fidelity(
                page_fidelity_results,
                threshold=self._source_fidelity_threshold,
            )
        else:
            asset_processing_seconds = 0.0
            try:
                response = self.pipeline.process_image(file_path)
            except FileNotFoundError as e:
                raise OCRServiceError(str(e), status_code=404)
            total_pages = 1
            image = cv2.imread(file_path)
            if image is None:
                raise OCRServiceError(f"Cannot read image: {file_path}")
            height, width = image.shape[:2]
            structured_page = build_structured_page(
                response,
                document_id,
                1,
                width,
                height,
                extraction_mode="ocr",
            )
            asset_started = time.perf_counter()
            preserve_page_assets(document_id, structured_page, image, self._asset_store)
            asset_processing_seconds += time.perf_counter() - asset_started
            structured_pages = [structured_page]
            source_fidelity = aggregate_source_fidelity(
                [evaluate_page(None, structured_page)],
                threshold=self._source_fidelity_threshold,
            )

        structured_document = StructuredDocument(
            document_id=document_id,
            total_pages=total_pages,
            pages=structured_pages,
            extraction_identity=cache_identity,
            source_fidelity=source_fidelity,
        )

        reconstruction_artifact = None
        try:
            reconstruction = self._reconstruction_service.reconstruct_document(structured_document)
            artifact = self._artifact_registry.register(document_id, reconstruction.output_path)
            reconstruction_artifact = {
                "artifact_id": artifact.artifact_id,
                "document_id": document_id,
                "file_name": "reconstructed.pdf",
                "content_type": "application/pdf",
                "size": artifact.path.stat().st_size,
                "render_seconds": reconstruction.render_seconds,
                "warnings": reconstruction.warnings,
            }
        except Exception:
            logger.exception("Reconstruction failed for document %s; preserving OCR result", document_id)

        memory_after = process_memory_mb()
        logger.info(
            "OCR source fidelity: document_id=%s fidelity=%s evaluable_pages=%d/%d "
            "native_lines=%d accounted=%d unaccounted=%d duplicates=%d codes=%s",
            document_id,
            source_fidelity.get("fidelity"),
            source_fidelity.get("evaluable_pages", 0),
            source_fidelity.get("total_pages", total_pages),
            source_fidelity.get("native_line_count", 0),
            source_fidelity.get("accounted_line_count", 0),
            source_fidelity.get("unaccounted_line_count", 0),
            source_fidelity.get("duplicate_line_count", 0),
            source_fidelity.get("codes", []),
        )
        logger.info(
            "OCR document completed: document_id=%s pages=%d duration_seconds=%.3f "
            "reconstruction_seconds=%s asset_seconds=%.6f working_set_mb=%s private_mb=%s",
            document_id, total_pages, time.perf_counter() - process_started,
            reconstruction_artifact.get("render_seconds") if reconstruction_artifact else None,
            asset_processing_seconds, memory_after[0], memory_after[1],
        )
        validation_errors = validate(response)
        for code in source_fidelity.get("codes", []):
            validation_errors.append({
                "component_type": "source",
                "text": f"Document source fidelity: {code}",
                "confidence": float(source_fidelity.get("fidelity") or 0.0),
                "reason": code,
            })
        result = {
            "schema_version": structured_document.schema_version,
            "document_id": document_id,
            "total_pages": total_pages,
            "page_average_confidence": response.page_average_confidence,
            "overall_level": evaluate(response, source_fidelity=source_fidelity),
            "components": evaluate_components(response),
            "validation_errors": validation_errors,
            "pages": [page.model_dump() for page in structured_document.pages],
            "asset_processing_seconds": round(asset_processing_seconds, 6),
            "reconstruction_artifact": reconstruction_artifact,
            "extraction_identity": cache_identity,
            "source_fidelity": source_fidelity,
        }

        # Lưu vào cache
        self._cache_put(file_path, result, cache_identity)

        return result
