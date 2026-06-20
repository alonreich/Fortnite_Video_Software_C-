from __future__ import annotations
import os
import sys
import pytest
sys.dont_write_bytecode = True
os.environ["PYTHONDONTWRITEBYTECODE"] = "1"

from collections import defaultdict
from dataclasses import dataclass
from datetime import datetime
import importlib
import ast
from pathlib import Path
import re
import shutil
import tempfile
import time
GREEN = "\x1b[32m"
RED = "\x1b[31m"
YELLOW = "\x1b[33m"
CYAN = "\x1b[36m"
RESET = "\x1b[0m"
@dataclass
class CaseOutcome:
    nodeid: str
    status: str
    detail: str
_OUTCOMES: dict[str, CaseOutcome] = {}
_TARGET_CACHE: dict[str, list[str]] = {}
_SCENARIO_CACHE: dict[str, dict[str, str]] = {}
_RAW_LONGREPR: dict[str, str] = {}
_TEST_INTEL_CACHE: dict[str, dict[str, dict[str, object]]] = {}
_PRIORITY = {"passed": 1, "skipped": 2, "failed": 3}
_SANITY_TMP_ROOT: Path | None = None
_ORIG_TEMP_ENV: dict[str, str | None] = {"TMP": None, "TEMP": None, "TMPDIR": None}
_RUN_STARTED_AT: str = ""

def _cleanup_disabled() -> bool:
    return os.environ.get("FVS_SANITY_NO_CLEANUP") == "1"

def _report_disabled() -> bool:
    return os.environ.get("FVS_SANITY_NO_REPORT") == "1"

def _cleanup_pytest_cache() -> None:
    if _cleanup_disabled():
        return
    cache_dir = Path(".pytest_cache")
    if cache_dir.exists():
        shutil.rmtree(cache_dir, ignore_errors=True)
    project_root = Path(__file__).resolve().parent.parent
    for pycache in project_root.rglob("__pycache__"):
        if pycache.is_dir():
            shutil.rmtree(pycache, ignore_errors=True)

def _snapshot_tmp_entries(tmp_dir: Path) -> set[str]:
    try:
        if tmp_dir.exists():
            return {p.name for p in tmp_dir.iterdir()}
    except Exception:
        pass
    return set()

def _remove_path_quietly(path: Path) -> None:
    try:
        if path.is_dir():
            shutil.rmtree(path, ignore_errors=True)
        elif path.exists():
            path.unlink(missing_ok=True)
    except Exception:
        pass

def _cleanup_new_tmp_entries(tmp_dir: Path, before_entries: set[str]) -> None:
    if _cleanup_disabled():
        return
    try:
        if not tmp_dir.exists():
            return
        after_entries = {p.name for p in tmp_dir.iterdir()}
        for name in sorted(after_entries - before_entries):
            _remove_path_quietly(tmp_dir / name)
    except Exception:
        pass

def _setup_tmp_sandbox(config) -> None:
    global _SANITY_TMP_ROOT
    temp_root = Path(tempfile.gettempdir())
    if not _cleanup_disabled():
        for stale_dir in temp_root.glob("tmp_fvs_sanity_pytest_*"):
            if stale_dir.is_dir():
                try:
                    shutil.rmtree(stale_dir, ignore_errors=True)
                except: pass
    root = temp_root / f"tmp_fvs_sanity_pytest_{os.getpid()}_{int(time.time() * 1000)}"
    try:
        root.mkdir(parents=True, exist_ok=True)
    except Exception:
        import uuid
        root = temp_root / f"tmp_fvs_sanity_{uuid.uuid4().hex[:8]}"
        root.mkdir(parents=True, exist_ok=True)
    _SANITY_TMP_ROOT = root
    config.option.basetemp = str(root)
    for k in _ORIG_TEMP_ENV:
        _ORIG_TEMP_ENV[k] = os.environ.get(k)
        os.environ[k] = str(root)
    tempfile.tempdir = str(root)

def _disable_pytest_cacheprovider(config) -> None:
    pm = config.pluginmanager
    plugin = pm.get_plugin("cacheprovider")
    if plugin is not None:
        pm.unregister(plugin, name="cacheprovider")

def _install_legacy_import_alias() -> None:
    """
    Backward compatibility: many sanity files import `sanity_tests.*`
    while the package on disk is `sanity_test`.
    """
    try:
        pkg = importlib.import_module("sanity_test")
        sys.modules.setdefault("sanity_tests", pkg)
        for mod in ("_ai_sanity_helpers", "_pending", "_real_sanity_harness"):
            sys.modules.setdefault(
                f"sanity_tests.{mod}",
                importlib.import_module(f"sanity_test.{mod}"),
            )
    except Exception:
        return

def pytest_configure(config) -> None:
    global _RUN_STARTED_AT
    _RUN_STARTED_AT = datetime.now().astimezone().strftime("%Y-%m-%d %H:%M:%S %Z%z")
    config.addinivalue_line("markers", "timeout(seconds): maximum expected runtime for long-running sanity tests")
    _disable_pytest_cacheprovider(config)
    _cleanup_pytest_cache()
    parent_dir = Path(__file__).parent
    if not _cleanup_disabled():
        for stale_dir in parent_dir.glob("tmp_fvs_sanity_pytest_*"):
            if stale_dir.is_dir():
                try:
                    shutil.rmtree(stale_dir, ignore_errors=True)
                except: pass
    _setup_tmp_sandbox(config)
    _install_legacy_import_alias()

def pytest_sessionfinish(session, exitstatus) -> None:
    _ = (session, exitstatus)
    _cleanup_pytest_cache()
    global _SANITY_TMP_ROOT
    if not _cleanup_disabled() and _SANITY_TMP_ROOT and _SANITY_TMP_ROOT.exists():
        shutil.rmtree(_SANITY_TMP_ROOT, ignore_errors=True)
    _SANITY_TMP_ROOT = None
    for k, v in _ORIG_TEMP_ENV.items():
        if v is None:
            os.environ.pop(k, None)
        else:
            os.environ[k] = v
    tempfile.tempdir = None
@pytest.fixture(autouse=True)
def _enforce_per_test_tmp_and_pycache_hygiene():
    """
    Enforce strict hygiene for every sanity test:
    - no bytecode cache leftovers
    - no temp leftovers in current %TMP% sandbox
    """
    tmp_dir = Path(tempfile.gettempdir())
    before_tmp_entries = _snapshot_tmp_entries(tmp_dir)
    yield
    _cleanup_pytest_cache()
    _cleanup_new_tmp_entries(tmp_dir, before_tmp_entries)

def _status_from_report(report) -> str | None:
    if report.when == "call":
        if report.passed:
            return "passed"
        if report.failed:
            return "failed"
        if report.skipped:
            return "skipped"
    if report.when == "setup" and report.skipped:
        return "skipped"
    if report.when in {"setup", "teardown"} and report.failed:
        return "failed"
    return None

def _first_non_empty_line(text: str) -> str:
    for line in text.splitlines():
        line = line.strip()
        if line:
            return line
    return ""

def _detail_from_report(report, status: str) -> str:
    if status == "passed":
        return "All assertions passed."
    if status == "skipped":
        detail = _extract_skip_reason(report)
        return detail or "Marked as pending/skipped."
    longrepr = getattr(report, "longreprtext", "") or ""
    if status == "failed":
        detail = _extract_failure_reason(longrepr)
    else:
        detail = _first_non_empty_line(longrepr)
    if detail:
        return detail
    return "Assertion failed."

def _extract_skip_reason(report) -> str:
    longrepr = getattr(report, "longrepr", None)
    if isinstance(longrepr, tuple) and len(longrepr) >= 3:
        return str(longrepr[2]).strip()
    text = getattr(report, "longreprtext", "") or ""
    for ln in text.splitlines():
        ln = ln.strip()
        if not ln:
            continue
        if "Skipped:" in ln:
            return ln.split("Skipped:", 1)[1].strip() or ln
        return ln
    return ""

def _extract_failure_reason(longrepr: str) -> str:
    if not longrepr:
        return "Assertion failed."
    lines = [ln.strip() for ln in longrepr.splitlines() if ln.strip()]
    for ln in lines:
        if "AssertionError:" in ln:
            return ln.split("AssertionError:", 1)[1].strip() or ln
    for ln in lines:
        if re.search(r"\b[A-Za-z_]+Error:\s*", ln):
            return ln
    for ln in reversed(lines):
        if ln.startswith("E"):
            candidate = re.sub(r"^E\s+", "", ln).strip()
            if candidate and not candidate.startswith("assert "):
                return candidate
    return _first_non_empty_line(longrepr)

def pytest_runtest_logreport(report) -> None:
    status = _status_from_report(report)
    if status is None:
        return
    detail = _detail_from_report(report, status)
    raw_longrepr = getattr(report, "longreprtext", "") or ""
    if raw_longrepr:
        _RAW_LONGREPR[report.nodeid] = raw_longrepr
    existing = _OUTCOMES.get(report.nodeid)
    if existing and _PRIORITY[existing.status] >= _PRIORITY[status]:
        return
    _OUTCOMES[report.nodeid] = CaseOutcome(nodeid=report.nodeid, status=status, detail=detail)

def _module_to_file_candidates(module_path: str) -> list[str]:
    mod = module_path.strip()
    if not mod or mod.startswith("sanity_test") or mod.startswith("sanity_tests"):
        return []
    if mod.split(".")[0] in {
        "os",
        "sys",
        "re",
        "types",
        "tempfile",
        "pathlib",
        "typing",
        "pytest",
        "dataclasses",
    }:
        return []
    candidates: list[str] = []
    as_file = Path(*mod.split(".")).with_suffix(".py")
    if as_file.exists():
        candidates.append(as_file.as_posix())
    package_init = Path(*mod.split(".")) / "__init__.py"
    if package_init.exists():
        candidates.append(package_init.as_posix())
    return candidates

def _infer_targets_for_test(test_file: str) -> list[str]:
    if test_file in _TARGET_CACHE:
        return _TARGET_CACHE[test_file]
    src = Path(test_file).read_text(encoding="utf-8", errors="ignore")
    targets: list[str] = []
    for path in re.findall(r"read_source\(\s*['\"]([^'\"]+)['\"]\s*\)", src):
        targets.append(path.replace("\\", "/"))
    import_paths = re.findall(r"^\s*from\s+([a-zA-Z_][\w\.]*)\s+import\s+", src, flags=re.MULTILINE)
    import_paths += re.findall(r"^\s*import\s+([a-zA-Z_][\w\.]*)", src, flags=re.MULTILINE)
    for mod in import_paths:
        targets.extend(_module_to_file_candidates(mod))
    for dotted in re.findall(r"monkeypatch\.setattr\(\s*['\"]([a-zA-Z_][\w\.]*)", src):
        module_only = dotted.rsplit(".", 1)[0] if "." in dotted else dotted
        targets.extend(_module_to_file_candidates(module_only))
    deduped: list[str] = []
    seen: set[str] = set()
    for t in targets:
        if t not in seen:
            seen.add(t)
            deduped.append(t)
    _TARGET_CACHE[test_file] = deduped
    return deduped

def _shorten(text: str, limit: int = 160) -> str:
    compact = " ".join((text or "").split())
    if len(compact) <= limit:
        return compact
    return compact[: limit - 3] + "..."

def _build_ai_fix_prompt(test_file: str, detail: str, status: str) -> str:
    targets = _infer_targets_for_test(test_file)
    if targets:
        focus = ", ".join(targets[:4])
    else:
        focus = "the modules exercised by this failing test"
    if status == "skipped":
        return f"Review {focus} and wire the missing integration/pending implementation so this test executes instead of being skipped."
    reason = _shorten(detail or "failing assertion")
    return f"Review {focus} and update the logic so it satisfies the failing check: {reason}."

def _humanize_test_name(test_name: str) -> str:
    title = test_name
    if title.startswith("test_"):
        title = title[5:]
    title = title.replace("_", " ").strip()
    if not title:
        return "General sanity verification scenario."
    return title[0].upper() + title[1:]

def _extract_pending_scenario(fn: ast.FunctionDef) -> str:
    for node in ast.walk(fn):
        if not isinstance(node, ast.Call):
            continue
        func_name = ""
        if isinstance(node.func, ast.Name):
            func_name = node.func.id
        elif isinstance(node.func, ast.Attribute):
            func_name = node.func.attr
        if func_name != "pending_test":
            continue
        if len(node.args) >= 2 and isinstance(node.args[1], ast.Constant) and isinstance(node.args[1].value, str):
            txt = node.args[1].value.strip()
            if txt:
                return txt
    return ""

def _scenarios_for_test_file(test_file: str) -> dict[str, str]:
    if test_file in _SCENARIO_CACHE:
        return _SCENARIO_CACHE[test_file]
    out: dict[str, str] = {}
    try:
        src = Path(test_file).read_text(encoding="utf-8", errors="ignore")
        tree = ast.parse(src)
        for node in tree.body:
            if not isinstance(node, ast.FunctionDef):
                continue
            if not node.name.startswith("test_"):
                continue
            scenario = (ast.get_docstring(node) or "").strip()
            if not scenario:
                scenario = _extract_pending_scenario(node)
            if not scenario:
                scenario = _humanize_test_name(node.name)
            out[node.name] = scenario
    except Exception:
        out = {}
    _SCENARIO_CACHE[test_file] = out
    return out

def _scenario_for_case(test_file: str, test_name: str) -> str:
    return _scenarios_for_test_file(test_file).get(test_name, _humanize_test_name(test_name))

def _call_name(node: ast.Call) -> str:
    if isinstance(node.func, ast.Name):
        return node.func.id
    if isinstance(node.func, ast.Attribute):
        return node.func.attr
    return ""

def _extract_assert_needles(fn: ast.FunctionDef) -> list[str]:
    needles: list[str] = []
    for node in ast.walk(fn):
        if not isinstance(node, ast.Call):
            continue
        if _call_name(node) != "assert_all_present":
            continue
        if len(node.args) < 2:
            continue
        seq = node.args[1]
        if not isinstance(seq, (ast.List, ast.Tuple)):
            continue
        for elt in seq.elts:
            if isinstance(elt, ast.Constant) and isinstance(elt.value, str):
                txt = elt.value.strip()
                if txt:
                    needles.append(txt)
    return needles

def _extract_assert_conditions(fn: ast.FunctionDef, src: str) -> list[str]:
    checks: list[str] = []
    for node in ast.walk(fn):
        if not isinstance(node, ast.Assert):
            continue
        seg = ast.get_source_segment(src, node.test) or ""
        seg = " ".join(seg.split())
        if seg:
            checks.append(seg)
    return checks

def _test_intel_for_file(test_file: str) -> dict[str, dict[str, object]]:
    if test_file in _TEST_INTEL_CACHE:
        return _TEST_INTEL_CACHE[test_file]
    out: dict[str, dict[str, object]] = {}
    try:
        src = Path(test_file).read_text(encoding="utf-8", errors="ignore")
        tree = ast.parse(src)
        for node in tree.body:
            if not isinstance(node, ast.FunctionDef) or not node.name.startswith("test_"):
                continue
            scenario = _scenario_for_case(test_file, node.name)
            out[node.name] = {
                "scenario": scenario,
                "needles": _extract_assert_needles(node),
                "asserts": _extract_assert_conditions(node, src),
            }
    except Exception:
        out = {}
    _TEST_INTEL_CACHE[test_file] = out
    return out

def _as_str_list(value: object) -> list[str]:
    if isinstance(value, (list, tuple, set)):
        out: list[str] = []
        for item in value:
            txt = str(item).strip()
            if txt:
                out.append(txt)
        return out
    return []

def _expected_behavior_for_case(test_file: str, test_name: str, scenario: str) -> str:
    intel = _test_intel_for_file(test_file).get(test_name, {})
    needles = _as_str_list(intel.get("needles", []))
    asserts = _as_str_list(intel.get("asserts", []))
    if needles:
        shown = "; ".join(_shorten(n, 140) for n in needles[:4])
        return (
            "This dryrun contract expects specific source-level logic/snippets to exist. "
            f"Key expected snippets include: {shown}."
        )
    if asserts:
        shown = "; ".join(_shorten(a, 140) for a in asserts[:4])
        return (
            "This runtime/behavior test expects all assertion conditions to hold under the scenario. "
            f"Key expected checks: {shown}."
        )
    return f"The tested scenario should complete successfully: {scenario}."

def _why_expected_for_case(test_file: str, scenario: str) -> str:
    name = Path(test_file).name.lower()
    if "_dryrun" in name:
        return (
            "Dryrun tests protect code contracts against silent refactors. "
            "If the expected snippets disappear/rename, runtime behavior can drift without immediate visibility."
        )
    if "challenge" in name:
        return (
            "Challenge tests validate extreme accidental scenarios, where race/timing/edge math issues are most likely to break user workflows."
        )
    if "core" in name:
        return (
            "Core tests enforce non-negotiable product behavior (sync, stability, persistence, safety) that must stay deterministic between releases."
        )
    return (
        "This sanity check guards user-facing behavior and ensures implementation changes do not break expected app flow."
    )

def _actual_behavior_for_case(case: CaseOutcome) -> str:
    if case.status == "passed":
        return "Observed result: all assertions/conditions in this test passed exactly as expected."
    if case.status == "skipped":
        return (
            "Observed result: test was skipped/pending, so behavior was not executed. "
            "No runtime validation occurred for this scenario in this run."
        )
    raw = _RAW_LONGREPR.get(case.nodeid, "")
    if "Missing expected snippets:" in case.detail:
        return (
            "Observed result: contract snippets were not found in target source files. "
            f"Failure detail: {case.detail}"
        )
    if raw:
        return f"Observed result: pytest failure trace indicates -> {_shorten(_extract_failure_reason(raw), 260)}"
    return f"Observed result: {case.detail}"

def _traceback_focus(case: CaseOutcome, max_lines: int = 4) -> list[str]:
    raw = _RAW_LONGREPR.get(case.nodeid, "")
    if not raw:
        return []
    lines = [ln.strip() for ln in raw.splitlines() if ln.strip()]
    interesting: list[str] = []
    for ln in lines:
        if "AssertionError:" in ln or re.search(r"\b[A-Za-z_]+Error:\s*", ln) or ln.startswith("E "):
            interesting.append(ln)
    if not interesting:
        interesting = lines[-max_lines:]
    return [_shorten(ln, 240) for ln in interesting[:max_lines]]

def _expected_vs_actual_gap(expected: str, actual: str, status: str) -> str:
    if status == "passed":
        return "No gap detected; expected and observed behavior are aligned."
    if status == "skipped":
        return "Execution gap: expected behavior was not validated because the test did not run."
    return (
        "Behavior gap detected: expected contract/runtime outcome did not match observed output. "
        "Use the AI instructions below to trace where implementation diverges from expectation."
    )

def _file_purpose_summary(test_file: str) -> str:
    name = Path(test_file).name.lower()
    if "_dryrun" in name:
        return (
            "Purpose: source-contract dryrun. This file validates that critical logic snippets/API wiring still exist in code after refactors."
        )
    if "challenge" in name:
        return (
            "Purpose: extreme challenge scenarios. This file stress-tests edge conditions and accidental-user behavior where robustness can break."
        )
    if "real_sanity" in name:
        return (
            "Purpose: real behavioral sanity checks. This file validates integration-level behavior and user-facing logic outcomes."
        )
    if "core" in name:
        return (
            "Purpose: core product contracts. This file protects foundational guarantees that should never regress."
        )
    return "Purpose: sanity verification for this feature area."

def _logic_hint(detail: str, status: str) -> str:
    if status == "skipped":
        return "This test is skipped/pending; wire the missing implementation path so the scenario executes."
    low = (detail or "").lower()
    if "missing expected snippets" in low:
        return "Dryrun contract drift: expected source snippets changed/refactored; align implementation or update test contract expectations."
    if "attributeerror" in low and "has no attribute" in low:
        return "Object wiring problem: a required attribute/method is missing before the tested call path executes."
    if "ffmpeg" in low:
        return "FFmpeg invocation path likely not reached; inspect branch guards and command-construction flow."
    if "assert" in low:
        return "Assertion mismatch: inspect conditional math/state transitions for this scenario and align behavior with expected contract."
    return "Inspect the control flow and state updates touched by this scenario; one of the guards/calculations likely regressed."

def _build_ai_fix_instructions(test_file: str, detail: str, status: str, scenario: str) -> list[str]:
    targets = _infer_targets_for_test(test_file)
    focus = ", ".join(targets[:6]) if targets else "the modules imported/monkeypatched by this test file"
    intel = _test_intel_for_file(test_file)
    sample_test = next(iter(intel.values()), {})
    needles = _as_str_list(sample_test.get("needles", []))
    asserts = _as_str_list(sample_test.get("asserts", []))
    contract_hint = ""
    if needles:
        contract_hint = f"Validate presence/behavior of contract snippets such as: {', '.join(_shorten(n, 90) for n in needles[:3])}."
    elif asserts:
        contract_hint = f"Trace assertion conditions such as: {', '.join(_shorten(a, 90) for a in asserts[:3])}."
    else:
        contract_hint = "Trace the exact branch and state transitions used by this test scenario."
    return [
        f"Open and inspect these probable files first: {focus}.",
        contract_hint,
        f"Focus on this logic area: {_logic_hint(detail, status)}",
        f"Reproduce the exact scenario under test: {scenario}",
        "Set temporary debug logs/breakpoints at the failing branch, compare expected vs actual values, and isolate first divergence.",
        "Apply a minimal targeted fix, then rerun this test file and the related dryrun/runtime pair.",
    ]

def _append_summary_report(grouped: dict[str, list[CaseOutcome]], total_passed: int, total_failed: int, total_skipped: int) -> None:
    if _report_disabled():
        return
    report_path = Path(__file__).resolve().parent.parent / "Report_Sanity_Test_Summary.txt"
    now_stamp = datetime.now().astimezone().strftime("%Y-%m-%d %H:%M:%S %Z%z")
    lines: list[str] = []
    lines.append("")
    lines.append("=" * 118)
    lines.append(f"SANITY TEST RUN STARTED: {_RUN_STARTED_AT or now_stamp}")
    lines.append(f"SANITY TEST RUN FINISHED: {now_stamp}")
    lines.append(f"OVERALL RESULT => WORKED: {total_passed} | FAILED: {total_failed} | SKIPPED: {total_skipped}")
    lines.append("=" * 118)
    for file_idx, test_file in enumerate(sorted(grouped), start=1):
        entries = sorted(grouped[test_file], key=lambda x: x.nodeid)
        passed = [e for e in entries if e.status == "passed"]
        failed = [e for e in entries if e.status == "failed"]
        skipped = [e for e in entries if e.status == "skipped"]
        file_result = "FAILED" if (failed or skipped) else "SUCCEEDED"
        lines.append("")
        lines.append(f"[{file_idx}] TEST FILE: {test_file}")
        lines.append(f"    Timestamp: {now_stamp}")
        lines.append(f"    File Status: {file_result}")
        lines.append(f"    Counts: passed={len(passed)} | failed={len(failed)} | skipped={len(skipped)}")
        lines.append(f"    {_file_purpose_summary(test_file)}")
        for case_idx, case in enumerate(entries, start=1):
            test_name = case.nodeid.split("::")[-1]
            scenario = _scenario_for_case(test_file, test_name)
            status_txt = "SUCCEEDED" if case.status == "passed" else ("FAILED" if case.status == "failed" else "FAILED (SKIPPED/PENDING)")
            expected = _expected_behavior_for_case(test_file, test_name, scenario)
            why_expected = _why_expected_for_case(test_file, scenario)
            actual = _actual_behavior_for_case(case)
            gap = _expected_vs_actual_gap(expected, actual, case.status)
            lines.append(f"    {case_idx}. Title: {test_name}")
            lines.append(f"       Status: {status_txt}")
            lines.append(f"       Test Scenario (Detailed): {scenario}")
            lines.append(f"       Expected Behavior: {expected}")
            lines.append(f"       Why This Is Expected: {why_expected}")
            lines.append(f"       Actual Behavior: {actual}")
            lines.append(f"       Expected vs Actual: {gap}")
            if case.status == "passed":
                lines.append("       Result Explanation: Scenario executed correctly and all checks aligned with the expected contract.")
            elif case.status == "failed":
                lines.append(f"       Why Failed (Primary): {case.detail}")
                traces = _traceback_focus(case)
                if traces:
                    lines.append("       Failure Evidence (traceback focus):")
                    for t in traces:
                        lines.append(f"         - {t}")
                lines.append("       AI Agent Instruction:")
                for i, step in enumerate(_build_ai_fix_instructions(test_file, case.detail, case.status, scenario), start=1):
                    lines.append(f"         {i}. {step}")
            else:
                lines.append(f"       Why Failed (Primary): {case.detail}")
                lines.append("       Failure Evidence (traceback focus):")
                lines.append("         - Test was skipped/pending, therefore no executable runtime evidence exists for this run.")
                lines.append("       AI Agent Instruction:")
                for i, step in enumerate(_build_ai_fix_instructions(test_file, case.detail, case.status, scenario), start=1):
                    lines.append(f"         {i}. {step}")
    report_path.parent.mkdir(parents=True, exist_ok=True)
    with report_path.open("a", encoding="utf-8") as f:
        f.write("\n".join(lines) + "\n")

def pytest_terminal_summary(terminalreporter, exitstatus, config) -> None:
    _ = (exitstatus, config)
    if not _OUTCOMES:
        return
    grouped: dict[str, list[CaseOutcome]] = defaultdict(list)
    for outcome in _OUTCOMES.values():
        test_file = outcome.nodeid.split("::", 1)[0]
        grouped[test_file].append(outcome)
    terminalreporter.write_sep("=", "SANITY END-USER REPORT")
    total_passed = 0
    total_failed = 0
    total_skipped = 0
    for test_file in sorted(grouped):
        entries = sorted(grouped[test_file], key=lambda x: x.nodeid)
        passed = [e for e in entries if e.status == "passed"]
        failed = [e for e in entries if e.status == "failed"]
        skipped = [e for e in entries if e.status == "skipped"]
        total_passed += len(passed)
        total_failed += len(failed)
        total_skipped += len(skipped)
        terminalreporter.write_line(f"{CYAN}{test_file}{RESET}")
        terminalreporter.write_line(f"  {GREEN}WORKED:{RESET}")
        if passed:
            for p in passed:
                test_name = p.nodeid.split("::")[-1]
                terminalreporter.write_line(f"    {GREEN}[PASS]{RESET} {test_name}")
        else:
            terminalreporter.write_line("    (none)")
        terminalreporter.write_line(f"  {RED}DID NOT PASS:{RESET}")
        if failed or skipped:
            for f in failed:
                test_name = f.nodeid.split("::")[-1]
                terminalreporter.write_line(f"    {RED}[FAIL]{RESET} {test_name} :: {_shorten(f.detail, 120)}")
                terminalreporter.write_line(
                    f"    {RED}AI FIX PROMPT:{RESET} {_build_ai_fix_prompt(test_file, f.detail, f.status)}"
                )
            for s in skipped:
                test_name = s.nodeid.split("::")[-1]
                terminalreporter.write_line(
                    f"    {RED}[FAIL]{RESET} {test_name} (SKIPPED/PENDING) :: {_shorten(s.detail, 120)}"
                )
                terminalreporter.write_line(
                    f"    {RED}AI FIX PROMPT:{RESET} {_build_ai_fix_prompt(test_file, s.detail, s.status)}"
                )
        else:
            terminalreporter.write_line("    (none)")
    try:
        _append_summary_report(grouped, total_passed, total_failed, total_skipped)
        if _report_disabled():
            terminalreporter.write_line(f"{CYAN}Summary file skipped:{RESET} FVS_SANITY_NO_REPORT=1")
        else:
            terminalreporter.write_line(
                f"{CYAN}Summary file appended:{RESET} Report_Sanity_Test_Summary.txt"
            )
    except Exception as ex:
        terminalreporter.write_line(f"{RED}Failed writing summary file:{RESET} {ex}")
    terminalreporter.write_sep(
        "=",
        f"OVERALL: {GREEN}{total_passed} worked{RESET} | {RED}{total_failed} failed{RESET} | {YELLOW}{total_skipped} skipped{RESET}",
    )
