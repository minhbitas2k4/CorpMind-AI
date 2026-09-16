from __future__ import annotations
import logging
import traceback
import asyncio
import time
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from fastapi.responses import FileResponse
from starlette.concurrency import run_in_threadpool

from ocr.paddle_engine import PaddleEngine
from ocr.pipeline import OCRPipeline
from services.ocr_service import OCRService, OCRServiceError
from schemas.ocr_api import OCRRequest, OCRSuccessResponse, OCRErrorResponse
from reconstruction.asset_store import LocalAssetStore
from reconstruction.reconstruction_service import ReconstructionService
from runtime_config import ensure_writable_directory, load_runtime_settings
from runtime_observability import process_memory_mb

logger = logging.getLogger("ocr")

ocr_service: OCRService | None = None
ocr_document_lock: asyncio.Lock | None = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global ocr_service, ocr_document_lock
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    settings = load_runtime_settings()
    ensure_writable_directory(settings.asset_root)
    ensure_writable_directory(settings.reconstruction_output_root)
    logger.info(
        "OCR configuration validated: language=%s dpi=%d extraction_mode=%s fidelity_threshold=%.3f omp_threads=%d mkl_threads=%d",
        settings.language, settings.render_dpi,
        settings.extraction_mode, settings.source_fidelity_threshold,
        settings.omp_num_threads, settings.mkl_num_threads,
    )
    logger.info("Đang load OCR engine (PP-Structure + PaddleOCR)...")
    engine = PaddleEngine(lang=settings.language, table=True)
    pipeline = OCRPipeline(engine=engine)
    reconstruction = ReconstructionService(settings.reconstruction_output_root)
    ocr_service = OCRService(
        pipeline=pipeline,
        asset_store=LocalAssetStore(settings.asset_root),
        reconstruction_service=reconstruction,
        render_dpi=settings.render_dpi,
        extraction_mode=settings.extraction_mode,
        source_fidelity_threshold=settings.source_fidelity_threshold,
    )
    ocr_document_lock = asyncio.Lock()
    logger.info("OCR engine đã sẵn sàng.")

    yield

    ocr_service = None
    ocr_document_lock = None
    logger.info("OCR service đã dừng.")


app = FastAPI(title="CorpMindAI OCR Service", lifespan=lifespan)


@app.exception_handler(Exception)
async def general_exception_handler(request: Request, exc: Exception):
    logger.error("Unhandled exception: %s\n%s", exc, traceback.format_exc())
    return JSONResponse(
        status_code=500,
        content={"error": "Internal server error"},
    )


@app.get("/")
def root():
    return {
        "service": "CorpMindAI OCR",
        "status": "running",
    }


@app.post("/ocr")
async def run_ocr(request: OCRRequest):
    if ocr_service is None or ocr_document_lock is None:
        raise HTTPException(status_code=503, detail="OCR service chưa sẵn sàng, thử lại sau.")

    request_started = time.perf_counter()
    try:
        async with ocr_document_lock:
            result = await run_in_threadpool(
                ocr_service.process, request.file_path, request.document_id
            )
    except OCRServiceError as e:
        memory = process_memory_mb()
        logger.warning(
            "OCR document failed: document_id=%s duration_seconds=%.3f reason=%s "
            "working_set_mb=%s private_mb=%s",
            request.document_id, time.perf_counter() - request_started, e.message,
            memory[0], memory[1],
        )
        error_response = OCRErrorResponse(
            document_id=request.document_id,
            error_message=e.message,
        )
        return JSONResponse(status_code=e.status_code, content=error_response.model_dump())
    except Exception as e:
        memory = process_memory_mb()
        logger.error(
            "Unexpected OCR failure: document_id=%s duration_seconds=%.3f "
            "working_set_mb=%s private_mb=%s\n%s",
            request.document_id, time.perf_counter() - request_started,
            memory[0], memory[1], traceback.format_exc(),
        )
        error_response = OCRErrorResponse(
            document_id=request.document_id,
            error_message="Unexpected OCR processing failure",
        )
        return JSONResponse(status_code=500, content=error_response.model_dump())

    success_response = OCRSuccessResponse(
        document_id=request.document_id,
        schema_version=result["schema_version"],
        total_pages=result.get("total_pages", 1),
        page_average_confidence=result["page_average_confidence"],
        overall_level=result["overall_level"],
        components=result["components"],
        validation_errors=result["validation_errors"],
        pages=result["pages"],
        asset_processing_seconds=result.get("asset_processing_seconds", 0.0),
        reconstruction_artifact=result.get("reconstruction_artifact"),
        extraction_identity=result.get("extraction_identity", {}),
        source_fidelity=result.get("source_fidelity", {}),
    )
    return success_response


@app.get("/internal/ocr-artifacts/{document_id}/{artifact_id}")
async def get_reconstruction_artifact(document_id: str, artifact_id: str):
    if ocr_service is None:
        raise HTTPException(status_code=503, detail="OCR service is not ready")
    artifact = ocr_service.artifact_registry.get(document_id, artifact_id)
    if artifact is None:
        raise HTTPException(status_code=404, detail="Reconstruction artifact not found")
    return FileResponse(
        artifact.path,
        media_type="application/pdf",
        filename="reconstructed.pdf",
    )


@app.delete("/internal/ocr-artifacts/{document_id}/{artifact_id}", status_code=204)
async def cleanup_reconstruction_artifact(document_id: str, artifact_id: str):
    if ocr_service is None:
        raise HTTPException(status_code=503, detail="OCR service is not ready")
    if not ocr_service.artifact_registry.cleanup(document_id, artifact_id):
        raise HTTPException(status_code=404, detail="Reconstruction artifact not found")
