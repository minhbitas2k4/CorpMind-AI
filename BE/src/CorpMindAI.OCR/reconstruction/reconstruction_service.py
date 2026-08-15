from __future__ import annotations

from pathlib import Path
import time

from models.ocr_result import ReconstructionResult, StructuredDocument
from reconstruction.asset_store import safe_identifier
from reconstruction.pdf_renderer import PDFRenderer


_DEFAULT_OUTPUT_DIR = Path(__file__).resolve().parent.parent / "reconstruction_outputs"


class ReconstructionService:
    def __init__(self, output_dir: str | Path = _DEFAULT_OUTPUT_DIR, renderer: PDFRenderer | None = None) -> None:
        self.output_dir = Path(output_dir).resolve()
        self.output_dir.mkdir(parents=True, exist_ok=True)
        self.renderer = renderer or PDFRenderer()

    def reconstruct_document(self, document: StructuredDocument) -> ReconstructionResult:
        safe_document = safe_identifier(document.document_id)
        directory = (self.output_dir / f"doc-{safe_document}").resolve()
        output_path = (directory / "reconstructed.pdf").resolve()
        if self.output_dir != output_path and self.output_dir not in output_path.parents:
            raise ValueError("reconstruction path escaped configured output root")
        directory.mkdir(parents=True, exist_ok=True)

        temporary_path = output_path.with_suffix(".tmp.pdf")
        started = time.perf_counter()
        try:
            warnings = self.renderer.render(document, temporary_path)
            temporary_path.replace(output_path)
        finally:
            temporary_path.unlink(missing_ok=True)
        render_seconds = time.perf_counter() - started
        return ReconstructionResult(
            document_id=document.document_id,
            output_path=str(output_path),
            total_pages=len(document.pages),
            render_seconds=round(render_seconds, 6),
            warnings=warnings,
        )
