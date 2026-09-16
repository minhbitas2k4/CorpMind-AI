from __future__ import annotations
from typing import Any, Optional
from enum import Enum
from pydantic import BaseModel, Field


class OCRBlock(BaseModel):
    text: str
    confidence: float
    bbox: list  # [[x1,y1],[x2,y2],[x3,y3],[x4,y4]]
    provenance: str = "ocr"

# PP-Structure component types
class ComponentType(str, Enum):
    TITLE = "title"
    TEXT = "text"
    TABLE = "table"
    FIGURE = "figure"
    FIGURE_CAPTION = "figure_caption"
    REFERENCE = "reference"
    LIST = "list" 
    UNKNOWN = "unknown"

# Layout component – kết quả sau khi PP-Structure phân tích layout
class LayoutComponent(BaseModel):
    component_type: ComponentType
    bbox: list                       
    order: int                      
    blocks: list[OCRBlock]           
    raw_text: str                    
    average_confidence: float
    table_html: Optional[str] = None 
    source_layout_type: str = "unknown"
    synthetic_region: bool = False
    extraction_provenance: str = "ocr"
    rows: Optional[list[dict[str, Any]]] = None
    cells: Optional[list[dict[str, Any]]] = None

# Response cho toàn trang (PP-Structure)
class StructureResponse(BaseModel):
    page_average_confidence: float
    components: list[LayoutComponent]

    def get_by_type(self, ctype: ComponentType) -> list[LayoutComponent]:
        return [c for c in self.components if c.component_type == ctype]

    def full_text(self) -> str:
        """Trả về toàn bộ text theo thứ tự layout."""
        return "\n\n".join(c.raw_text for c in self.components if c.raw_text)

class StructuredLine(BaseModel):
    line_id: str
    text: str
    confidence: float
    bbox: list
    normalized_bbox: list
    provenance: str = "ocr"


class ComponentMetadata(BaseModel):
    source_layout_type: str
    synthetic_region: bool = False
    asset_errors: list[str] = Field(default_factory=list)
    extraction_provenance: str = "ocr"


class AssetMetadata(BaseModel):
    asset_id: str
    type: str = "image"
    path: str
    width: int
    height: int
    source: str = "page_crop"


class FigureCaption(BaseModel):
    text: str
    lines: list[StructuredLine]


class StructuredComponent(BaseModel):
    component_id: str
    document_id: str
    page_number: int
    type: str
    bbox: list
    normalized_bbox: list
    reading_order: int
    confidence: float
    text: str
    lines: list[StructuredLine]
    metadata: ComponentMetadata
    rows: Optional[list[dict[str, Any]]] = None
    cells: Optional[list[dict[str, Any]]] = None
    asset: Optional[AssetMetadata] = None
    caption: Optional[FigureCaption] = None


class CoordinateSystem(BaseModel):
    origin: str = "top_left"
    bbox_format: str = "xyxy"
    unit: str = "pixel"


class StructuredPage(BaseModel):
    page_number: int
    width: int
    height: int
    render_dpi: int = 200
    coordinate_system: CoordinateSystem = Field(default_factory=CoordinateSystem)
    components: list[StructuredComponent]
    extraction_mode: str = "ocr"
    native_line_count: int = 0
    accounted_line_count: int = 0
    source_fidelity: float | None = None
    fidelity_codes: list[str] = Field(default_factory=list)


class StructuredDocument(BaseModel):
    schema_version: str = "1.1"
    document_id: str
    total_pages: int
    pages: list[StructuredPage]
    extraction_identity: dict[str, str] = Field(default_factory=dict)
    source_fidelity: dict[str, Any] = Field(default_factory=dict)


class ReconstructionResult(BaseModel):
    document_id: str
    output_path: str
    total_pages: int
    render_seconds: float
    warnings: list[str] = Field(default_factory=list)
