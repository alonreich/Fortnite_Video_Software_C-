from __future__ import annotations
import pytest

def pending_test(requirement_id: str, description: str) -> None:
    """Mark a sanity test as intentionally pending until harness wiring exists."""
    pytest.skip(f"[{requirement_id}] {description} (pending integration harness)")

