from __future__ import annotations
import hashlib
import json
import logging
import os
import threading
from typing import Any, Optional

logger = logging.getLogger("ocr.cache")

_DEFAULT_CACHE_DIR = os.path.join(os.path.dirname(__file__), "..", ".ocr_cache")


class OCRResultCache:
    def __init__(self, cache_dir: str = _DEFAULT_CACHE_DIR, max_entries: int = 500):
        self._cache_dir = os.path.abspath(cache_dir)
        self._max_entries = max_entries
        self._memory: dict[str, dict] = {}
        self._lock = threading.Lock()
        os.makedirs(self._cache_dir, exist_ok=True)
        self._load_disk_cache()

    def get(self, file_path: str, cache_identity: dict[str, Any] | str | None = None) -> Optional[dict]:
        """Trả về cached result nếu file hash trùng, None nếu miss."""
        key = self._cache_key(file_path, cache_identity)
        if key is None:
            return None

        with self._lock:
            entry = self._memory.get(key)
        if entry is None:
            return None

        # Kiểm tra TTL (24h)
        import time
        if time.time() - entry.get("timestamp", 0) > 86400:
            logger.info("Cache expired for %s", file_path)
            self._evict(key)
            return None

        logger.info("Cache HIT for %s (hash=%s)", file_path, key[:12])
        return entry.get("result")

    def put(
        self,
        file_path: str,
        result: dict,
        cache_identity: dict[str, Any] | str | None = None,
    ) -> None:
        """Lưu kết quả OCR vào cache (in-memory + disk)."""
        key = self._cache_key(file_path, cache_identity)
        if key is None:
            return

        import time
        entry = {"result": result, "timestamp": time.time(), "file": os.path.basename(file_path)}

        with self._lock:
            self._memory[key] = entry
            # Evict nếu quá max
            if len(self._memory) > self._max_entries:
                oldest_key = min(
                    self._memory,
                    key=lambda k: self._memory[k].get("timestamp", 0),
                )
                del self._memory[oldest_key]

        # Ghi disk background
        threading.Thread(target=self._write_disk, args=(key, entry), daemon=True).start()

    def clear(self) -> None:
        """Xóa toàn bộ cache."""
        with self._lock:
            self._memory.clear()
        import shutil
        if os.path.isdir(self._cache_dir):
            shutil.rmtree(self._cache_dir)
            os.makedirs(self._cache_dir, exist_ok=True)
        logger.info("Cache cleared")

    @property
    def size(self) -> int:
        with self._lock:
            return len(self._memory)

    def _hash_file(self, file_path: str) -> Optional[str]:
        """Tính SHA-256 hash của file content."""
        if not os.path.isfile(file_path):
            return None
        h = hashlib.sha256()
        try:
            with open(file_path, "rb") as f:
                for chunk in iter(lambda: f.read(8192), b""):
                    h.update(chunk)
            return h.hexdigest()
        except OSError as e:
            logger.warning("Cannot hash file %s: %s", file_path, e)
            return None

    @staticmethod
    def _identity_text(cache_identity: dict[str, Any] | str | None) -> str:
        if cache_identity is None:
            return ""
        if isinstance(cache_identity, str):
            return cache_identity
        return json.dumps(cache_identity, sort_keys=True, separators=(",", ":"), ensure_ascii=True)

    def _cache_key(self, file_path: str, cache_identity: dict[str, Any] | str | None = None) -> Optional[str]:
        file_hash = self._hash_file(file_path)
        if file_hash is None:
            return None
        identity = self._identity_text(cache_identity)
        if not identity:
            return file_hash
        return hashlib.sha256(f"{file_hash}|{identity}".encode("utf-8")).hexdigest()

    def _disk_path(self, key: str) -> str:
        return os.path.join(self._cache_dir, f"{key}.json")

    def _write_disk(self, key: str, entry: dict) -> None:
        try:
            path = self._disk_path(key)
            with open(path, "w", encoding="utf-8") as f:
                json.dump(entry, f, ensure_ascii=False)
        except OSError as e:
            logger.warning("Failed to write cache disk: %s", e)

    def _load_disk_cache(self) -> None:
        """Load cache từ disk vào memory khi service khởi động."""
        loaded = 0
        import time
        now = time.time()
        try:
            for fname in os.listdir(self._cache_dir):
                if not fname.endswith(".json"):
                    continue
                key = fname[:-5]
                fpath = os.path.join(self._cache_dir, fname)
                try:
                    with open(fpath, "r", encoding="utf-8") as f:
                        entry = json.load(f)
                    # Skip expired
                    if now - entry.get("timestamp", 0) > 86400:
                        os.remove(fpath)
                        continue
                    with self._lock:
                        self._memory[key] = entry
                    loaded += 1
                except (json.JSONDecodeError, OSError):
                    os.remove(fpath)
        except OSError as e:
            logger.warning("Failed to load disk cache: %s", e)

        if loaded:
            logger.info("Loaded %d cached OCR results from disk", loaded)

    def _evict(self, key: str) -> None:
        with self._lock:
            self._memory.pop(key, None)
        fpath = self._disk_path(key)
        try:
            os.remove(fpath)
        except OSError:
            pass
