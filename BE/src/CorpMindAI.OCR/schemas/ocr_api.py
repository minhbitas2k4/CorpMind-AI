from __future__ import annotations
from typing import Literal
from pydantic import BaseModel


class OCRRequest(BaseModel):
    document_id: str
    file_path: str


class ComponentResult(BaseModel):
    component_type: str
    raw_text: str
    average_confidence: float
    level: str
    bbox: list


class ValidationErrorItem(BaseModel):
    component_type: str
    text: str
    confidence: float
    reason: str


class OCRSuccessResponse(BaseModel):
    document_id: str
    status: Literal["success"] = "success"
    total_pages: int = 1
    page_average_confidence: float
    overall_level: str
    components: list[ComponentResult]
    validation_errors: list[ValidationErrorItem]


class OCRErrorResponse(BaseModel):
    document_id: str
    status: Literal["error"] = "error"
    error_message: str