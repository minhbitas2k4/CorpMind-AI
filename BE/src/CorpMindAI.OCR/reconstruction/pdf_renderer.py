from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

from reportlab import rl_config
from reportlab.pdfbase import pdfmetrics
from reportlab.pdfbase.ttfonts import TTFont
from reportlab.pdfgen import canvas

from models.ocr_result import StructuredComponent, StructuredDocument, StructuredLine, StructuredPage


_FONT_REGULAR = "ReconstructionVera"
_FONT_BOLD = "ReconstructionVeraBold"


def _register_fonts() -> None:
    if _FONT_REGULAR in pdfmetrics.getRegisteredFontNames():
        return
    font_dir = next(
        Path(item) for item in rl_config.TTFSearchPath
        if (Path(item) / "Vera.ttf").is_file() and (Path(item) / "VeraBd.ttf").is_file()
    )
    pdfmetrics.registerFont(TTFont(_FONT_REGULAR, str(font_dir / "Vera.ttf")))
    pdfmetrics.registerFont(TTFont(_FONT_BOLD, str(font_dir / "VeraBd.ttf")))


@dataclass(frozen=True)
class CoordinateTransform:
    pixel_width: float
    pixel_height: float
    pdf_width: float
    pdf_height: float

    @property
    def scale_x(self) -> float:
        return self.pdf_width / self.pixel_width

    @property
    def scale_y(self) -> float:
        return self.pdf_height / self.pixel_height

    def point(self, x: float, y: float) -> tuple[float, float]:
        return float(x) * self.scale_x, self.pdf_height - float(y) * self.scale_y

    def bbox(self, bbox: list) -> tuple[float, float, float, float]:
        """Convert top-left pixel xyxy to bottom-left PDF x, y, width, height."""
        x1, y1, x2, y2 = map(float, bbox)
        pdf_x = x1 * self.scale_x
        pdf_y = self.pdf_height - y2 * self.scale_y
        return pdf_x, pdf_y, (x2 - x1) * self.scale_x, (y2 - y1) * self.scale_y


class PDFRenderer:
    def render(self, document: StructuredDocument, output_path: str | Path) -> list[str]:
        _register_fonts()
        path = Path(output_path)
        path.parent.mkdir(parents=True, exist_ok=True)
        warnings: list[str] = []
        pdf = canvas.Canvas(str(path), pageCompression=1)
        for page in sorted(document.pages, key=lambda item: item.page_number):
            self._render_page(pdf, page, warnings)
            pdf.showPage()
        pdf.save()
        return warnings

    def _render_page(self, pdf: canvas.Canvas, page: StructuredPage, warnings: list[str]) -> None:
        pdf_width = page.width * 72.0 / page.render_dpi
        pdf_height = page.height * 72.0 / page.render_dpi
        pdf.setPageSize((pdf_width, pdf_height))
        transform = CoordinateTransform(page.width, page.height, pdf_width, pdf_height)
        ordered = sorted(page.components, key=lambda item: (item.reading_order, item.component_id))

        asset_types = {"table", "figure", "unknown"}
        for component in [item for item in ordered if item.type in asset_types]:
            if not self._render_asset(pdf, component, transform, warnings):
                self._render_component_text(pdf, component, transform, warnings, bold=False)

        for component in [item for item in ordered if item.type in {"text", "list"}]:
            self._render_component_text(pdf, component, transform, warnings, bold=False)

        for component in [item for item in ordered if item.type == "title"]:
            self._render_component_text(pdf, component, transform, warnings, bold=True)

        for component in [item for item in ordered if item.type == "figure" and item.caption is not None]:
            for line in component.caption.lines:
                self._render_line(pdf, line, transform, bold=False)

    def _render_asset(
        self,
        pdf: canvas.Canvas,
        component: StructuredComponent,
        transform: CoordinateTransform,
        warnings: list[str],
    ) -> bool:
        if component.asset is None:
            warnings.append(f"{component.component_id}: missing {component.type} asset")
            return False
        path = Path(component.asset.path)
        if not path.is_file():
            warnings.append(f"{component.component_id}: asset not found: {path}")
            return False
        try:
            x, y, width, height = transform.bbox(component.bbox)
            if width <= 0 or height <= 0:
                raise ValueError("component bbox has no renderable area")
            pdf.drawImage(str(path), x, y, width=width, height=height, preserveAspectRatio=False, mask="auto")
            return True
        except Exception as error:
            warnings.append(f"{component.component_id}: asset render failed: {error}")
            return False

    def _render_component_text(
        self,
        pdf: canvas.Canvas,
        component: StructuredComponent,
        transform: CoordinateTransform,
        warnings: list[str],
        bold: bool,
    ) -> None:
        try:
            if component.lines:
                for line in component.lines:
                    self._render_line(pdf, line, transform, bold=bold)
            elif component.text:
                synthetic = StructuredLine(
                    line_id=f"{component.component_id}-fallback",
                    text=component.text,
                    confidence=component.confidence,
                    bbox=[
                        [component.bbox[0], component.bbox[1]],
                        [component.bbox[2], component.bbox[1]],
                        [component.bbox[2], component.bbox[3]],
                        [component.bbox[0], component.bbox[3]],
                    ],
                    normalized_bbox=[],
                )
                self._render_line(pdf, synthetic, transform, bold=bold)
        except Exception as error:
            warnings.append(f"{component.component_id}: text render failed: {error}")

    @staticmethod
    def _line_rectangle(line: StructuredLine) -> list[float]:
        xs = [float(point[0]) for point in line.bbox]
        ys = [float(point[1]) for point in line.bbox]
        return [min(xs), min(ys), max(xs), max(ys)]

    def _render_line(
        self,
        pdf: canvas.Canvas,
        line: StructuredLine,
        transform: CoordinateTransform,
        bold: bool,
    ) -> None:
        if not line.text or not line.bbox:
            return
        x, y, width, height = transform.bbox(self._line_rectangle(line))
        if width <= 0 or height <= 0:
            return
        font_name = _FONT_BOLD if bold else _FONT_REGULAR
        maximum = 40.0 if bold else 32.0
        font_size = max(4.0, min(maximum, height * (0.88 if bold else 0.82)))
        text_width = pdfmetrics.stringWidth(line.text, font_name, font_size)
        if text_width > width and text_width > 0:
            font_size = max(3.0, font_size * width / text_width)

        pdf.saveState()
        clipping_path = pdf.beginPath()
        clipping_path.rect(x, y, width, height)
        pdf.clipPath(clipping_path, stroke=0, fill=0)
        pdf.setFont(font_name, font_size)
        baseline = y + max(0.0, (height - font_size) * 0.45)
        pdf.drawString(x, baseline, line.text)
        pdf.restoreState()
