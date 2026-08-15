from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path
from threading import Lock
from uuid import UUID, uuid4


@dataclass(frozen=True)
class ReconstructionArtifact:
    artifact_id: str
    document_id: str
    path: Path


class ReconstructionArtifactRegistry:
    """Process-local capability registry for renderer-owned PDF artifacts."""

    def __init__(self, output_root: str | Path) -> None:
        self.output_root = Path(output_root).resolve()
        self.output_root.mkdir(parents=True, exist_ok=True)
        self._artifacts: dict[str, ReconstructionArtifact] = {}
        self._lock = Lock()

    def register(self, document_id: str, output_path: str | Path) -> ReconstructionArtifact:
        path = Path(output_path).resolve()
        if self.output_root != path and self.output_root not in path.parents:
            raise ValueError("reconstruction artifact escaped configured output root")
        if not path.is_file():
            raise FileNotFoundError("reconstruction artifact does not exist")
        artifact = ReconstructionArtifact(str(uuid4()), str(document_id), path)
        with self._lock:
            self._artifacts[artifact.artifact_id] = artifact
        return artifact

    def get(self, document_id: str, artifact_id: str) -> ReconstructionArtifact | None:
        try:
            canonical_id = str(UUID(artifact_id))
        except (ValueError, AttributeError):
            return None
        with self._lock:
            artifact = self._artifacts.get(canonical_id)
        if artifact is None or artifact.document_id != str(document_id):
            return None
        path = artifact.path.resolve()
        if self.output_root != path and self.output_root not in path.parents:
            return None
        return artifact if path.is_file() else None

    def cleanup(self, document_id: str, artifact_id: str) -> bool:
        artifact = self.get(document_id, artifact_id)
        if artifact is None:
            return False
        artifact.path.unlink(missing_ok=True)
        try:
            artifact.path.parent.rmdir()
        except OSError:
            pass
        with self._lock:
            self._artifacts.pop(artifact.artifact_id, None)
        return True
