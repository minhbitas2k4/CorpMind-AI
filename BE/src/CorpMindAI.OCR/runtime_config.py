from __future__ import annotations

from dataclasses import dataclass
import os
from pathlib import Path
import tempfile


_PROJECT_ROOT = Path(__file__).resolve().parent


@dataclass(frozen=True)
class RuntimeSettings:
    language: str
    render_dpi: int
    extraction_mode: str
    source_fidelity_threshold: float
    asset_root: Path
    reconstruction_output_root: Path
    omp_num_threads: int
    mkl_num_threads: int


def load_runtime_settings() -> RuntimeSettings:
    settings = RuntimeSettings(
        language=os.getenv("OCR_LANGUAGE", "en").strip().lower(),
        render_dpi=int(os.getenv("OCR_RENDER_DPI", "200")),
        extraction_mode=os.getenv("OCR_EXTRACTION_MODE", "native_hybrid").strip().lower(),
        source_fidelity_threshold=float(os.getenv("OCR_SOURCE_FIDELITY_THRESHOLD", "1.0")),
        asset_root=Path(
            os.getenv("OCR_ASSET_ROOT", str(_PROJECT_ROOT / "reconstruction_assets"))
        ).resolve(),
        reconstruction_output_root=Path(
            os.getenv(
                "OCR_RECONSTRUCTION_OUTPUT_ROOT",
                str(_PROJECT_ROOT / "reconstruction_outputs"),
            )
        ).resolve(),
        omp_num_threads=int(os.getenv("OMP_NUM_THREADS", "1")),
        mkl_num_threads=int(os.getenv("MKL_NUM_THREADS", "1")),
    )
    if settings.language != "en":
        raise ValueError("OCR_LANGUAGE must remain 'en' for this English-only service")
    if settings.render_dpi != 200:
        raise ValueError("OCR_RENDER_DPI must remain 200 for the validated V1 pipeline")
    if settings.extraction_mode not in {"native_hybrid", "raster_only"}:
        raise ValueError("OCR_EXTRACTION_MODE must be 'native_hybrid' or 'raster_only'")
    if not 0.0 <= settings.source_fidelity_threshold <= 1.0:
        raise ValueError("OCR_SOURCE_FIDELITY_THRESHOLD must be between zero and one")
    if settings.omp_num_threads < 1 or settings.mkl_num_threads < 1:
        raise ValueError("OMP_NUM_THREADS and MKL_NUM_THREADS must be positive integers")
    return settings


def ensure_writable_directory(path: Path) -> None:
    path.mkdir(parents=True, exist_ok=True)
    if not path.is_dir():
        raise NotADirectoryError(f"Configured storage root is not a directory: {path}")
    probe = None
    try:
        with tempfile.NamedTemporaryFile(prefix=".corpmind-write-", dir=path, delete=False) as handle:
            probe = Path(handle.name)
    finally:
        if probe is not None:
            probe.unlink(missing_ok=True)
