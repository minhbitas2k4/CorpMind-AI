from __future__ import annotations

import os


def process_memory_mb() -> tuple[float | None, float | None]:
    """Return working-set/private MB without adding a profiling dependency."""
    if os.name != "nt":
        try:
            import resource
            rss = resource.getrusage(resource.RUSAGE_SELF).ru_maxrss
            return round(rss / 1024, 1), None
        except (ImportError, OSError):
            return None, None

    try:
        import ctypes
        from ctypes import wintypes

        class Counters(ctypes.Structure):
            _fields_ = [
                ("cb", wintypes.DWORD), ("page_faults", wintypes.DWORD),
                ("peak_working_set", ctypes.c_size_t), ("working_set", ctypes.c_size_t),
                ("quota_peak_paged", ctypes.c_size_t), ("quota_paged", ctypes.c_size_t),
                ("quota_peak_nonpaged", ctypes.c_size_t), ("quota_nonpaged", ctypes.c_size_t),
                ("pagefile", ctypes.c_size_t), ("peak_pagefile", ctypes.c_size_t),
                ("private", ctypes.c_size_t),
            ]

        counters = Counters()
        counters.cb = ctypes.sizeof(counters)
        get_process = ctypes.windll.kernel32.GetCurrentProcess
        get_process.restype = wintypes.HANDLE
        get_memory = ctypes.windll.psapi.GetProcessMemoryInfo
        get_memory.argtypes = [wintypes.HANDLE, ctypes.c_void_p, wintypes.DWORD]
        process = get_process()
        if not get_memory(
            process, ctypes.byref(counters), counters.cb
        ):
            return None, None
        mb = 1024 * 1024
        return round(counters.working_set / mb, 1), round(counters.private / mb, 1)
    except (AttributeError, OSError, ctypes.ArgumentError):
        return None, None
