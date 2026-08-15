from __future__ import annotations
from typing import Literal
from pydantic import BaseModel, Field
from models.ocr_result import StructuredPage


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


class ReconstructionArtifactInfo(BaseModel):
    artifact_id: str
    document_id: str
    file_name: str = "reconstructed.pdf"
    content_type: str = "application/pdf"
    size: int
    render_seconds: float
    warnings: list[str] = Field(default_factory=list)


class OCRSuccessResponse(BaseModel):
    document_id: str
    schema_version: str = "1.0"
    status: Literal["success"] = "success"
    total_pages: int = 1
    page_average_confidence: float
    overall_level: str
    components: list[ComponentResult]
    validation_errors: list[ValidationErrorItem]
    pages: list[StructuredPage] = Field(default_factory=list)
    asset_processing_seconds: float = 0.0
    reconstruction_artifact: ReconstructionArtifactInfo | None = None


class OCRErrorResponse(BaseModel):
    document_id: str
    status: Literal["error"] = "error"
    error_message: str
