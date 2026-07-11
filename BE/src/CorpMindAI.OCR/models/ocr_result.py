from __future__ import annotations
from typing import Optional
from enum import Enum
from pydantic import BaseModel


# ---------------------------------------------------------------------------
# Primitive block (một dòng text từ PaddleOCR / PP-Structure)
# ---------------------------------------------------------------------------
class OCRBlock(BaseModel):
    text: str
    confidence: float
    bbox: list  # [[x1,y1],[x2,y2],[x3,y3],[x4,y4]]


# ---------------------------------------------------------------------------
# PP-Structure component types
# ---------------------------------------------------------------------------
class ComponentType(str, Enum):
    TITLE = "title"
    TEXT = "text"
    TABLE = "table"
    FIGURE = "figure"
    FIGURE_CAPTION = "figure_caption"
    REFERENCE = "reference"
    LIST = "list" 
    UNKNOWN = "unknown"


# ---------------------------------------------------------------------------
# Layout component – kết quả sau khi PP-Structure phân tích layout
# ---------------------------------------------------------------------------
class LayoutComponent(BaseModel):
    component_type: ComponentType
    bbox: list                       # bounding box của vùng layout [x1, y1, x2, y2]
    order: int                       # thứ tự xuất hiện trên trang
    blocks: list[OCRBlock]           # các dòng text bên trong component
    raw_text: str                    # toàn bộ text của component (join blocks)
    average_confidence: float
    table_html: Optional[str] = None # chỉ dùng khi component_type == TABLE


# ---------------------------------------------------------------------------
# Response cho toàn trang (PP-Structure)
# ---------------------------------------------------------------------------
class StructureResponse(BaseModel):
    page_average_confidence: float
    components: list[LayoutComponent]

    def get_by_type(self, ctype: ComponentType) -> list[LayoutComponent]:
        return [c for c in self.components if c.component_type == ctype]

    def full_text(self) -> str:
        """Trả về toàn bộ text theo thứ tự layout."""
        return "\n\n".join(c.raw_text for c in self.components if c.raw_text)


# ---------------------------------------------------------------------------
# Response cũ (giữ lại để backward-compat với pipeline cơ bản)
# ---------------------------------------------------------------------------
# class OCRResponse(BaseModel):
#     average_confidence: float
#     blocks: list[OCRBlock]