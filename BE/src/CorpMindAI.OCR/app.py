from __future__ import annotations
import logging
import traceback
from contextlib import asynccontextmanager

from fastapi import FastAPI, HTTPException, Request
from fastapi.responses import JSONResponse
from starlette.concurrency import run_in_threadpool

from ocr.paddle_engine import PaddleEngine
from ocr.pipeline import OCRPipeline
from services.ocr_service import OCRService, OCRServiceError
from schemas.ocr_api import OCRRequest, OCRSuccessResponse, OCRErrorResponse

logger = logging.getLogger("ocr")

ocr_service: OCRService | None = None


@asynccontextmanager
async def lifespan(app: FastAPI):
    global ocr_service
    logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(name)s: %(message)s")
    logger.info("Đang load OCR engine (PP-Structure + PaddleOCR)...")
    engine = PaddleEngine()
    pipeline = OCRPipeline(engine=engine)
    ocr_service = OCRService(pipeline=pipeline)
    logger.info("OCR engine đã sẵn sàng.")

    yield

    ocr_service = None
    logger.info("OCR service đã dừng.")


app = FastAPI(title="CorpMindAI OCR Service", lifespan=lifespan)


@app.exception_handler(Exception)
async def general_exception_handler(request: Request, exc: Exception):
    logger.error("Unhandled exception: %s\n%s", exc, traceback.format_exc())
    return JSONResponse(
        status_code=500,
        content={
            "error": "Internal server error",
            "detail": str(exc),
        },
    )


@app.get("/")
def root():
    return {
        "service": "CorpMindAI OCR",
        "status": "running",
    }


@app.post("/ocr")
async def run_ocr(request: OCRRequest):
    if ocr_service is None:
        raise HTTPException(status_code=503, detail="OCR service chưa sẵn sàng, thử lại sau.")

    try:
        # OCR là CPU-bound nặng -> chạy trong threadpool để không block
        # event loop của FastAPI (cho phép nhận request khác song song).
        result = await run_in_threadpool(ocr_service.process, request.file_path)
    except OCRServiceError as e:
        logger.warning("OCR service error for document %s: %s", request.document_id, e.message)
        error_response = OCRErrorResponse(
            document_id=request.document_id,
            error_message=e.message,
        )
        return JSONResponse(status_code=e.status_code, content=error_response.model_dump())
    except Exception as e:
        logger.error("Unexpected error processing document %s: %s\n%s",
                      request.document_id, e, traceback.format_exc())
        error_response = OCRErrorResponse(
            document_id=request.document_id,
            error_message=f"Lỗi xử lý OCR: {e}",
        )
        return JSONResponse(status_code=500, content=error_response.model_dump())

    success_response = OCRSuccessResponse(
        document_id=request.document_id,
        total_pages=result.get("total_pages", 1),
        page_average_confidence=result["page_average_confidence"],
        overall_level=result["overall_level"],
        components=result["components"],
        validation_errors=result["validation_errors"],
    )
    return success_response