import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO CACHE. BOTH LINES, BEFORE EVERY OTHER IMPORT. This file previously had NEITHER guard.
# Running it directly was safe (Python does not cache the __main__ module), but importing it
# from anywhere would have dropped a __pycache__ beside it. `dont_write_bytecode` must sit above
# the imports because it only governs imports made after it; the env var covers child processes.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import shutil
import subprocess
from pathlib import Path

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

def is_binary(file_path: Path) -> bool:
    try:
        with open(file_path, 'rb') as f:
            chunk = f.read(2048)
            if b'\0' in chunk:
                return True
    except Exception:
        pass
    return False

def get_group_name(file_path: Path, project_root: Path) -> str:
    ext = file_path.suffix.lower()
    rel_path = str(file_path.relative_to(project_root)).lower()

    if ext in ['.axaml', '.xaml']:
        return "01_UI_Markup"
    
    if ext == '.cs':
        if "fortnitevideosoftware.core" in rel_path:
            return "03_Core_Logic"
        return "02_App_Code"
        
    if ext in ['.json', '.json5', '.xml', '.csproj', '.sln', '.config', '.props', '.targets', '.ini', '.toml', '.ruleset']:
        return "04_Configuration"
        
    if ext in ['.ico', '.png', '.jpg', '.jpeg', '.svg', '.dll', '.exe', '.traineddata']:
        return "05_Assets_and_Binaries"
        
    if ext in ['.py', '.cmd', '.bat', '.ps1', '.md', '.txt', '.yaml', '.yml', '.editorconfig', '.gitignore', '.gitattributes']:
        return "06_Scripts_and_Docs"

    return "07_Misc"

MAX_CODE_FILES = 9  # 9 code bundles + 1 tree file = 10 files max upload limit
MIN_PART_CHARS = 500_000  # Avoid splitting small projects into tiny fragments unnecessarily


def pack_into_capped_parts(items, max_parts=9):
    """Packs (rel_path, block) tuples across at most max_parts bundles."""
    if not items:
        return []

    total_chars = sum(len(b) for _, b in items)
    target_limit = max(MIN_PART_CHARS, (total_chars // max_parts) + 1)

    parts = []
    current_items = []
    used = 0

    for rel_path, block in items:
        if current_items and (used + len(block) > target_limit) and (len(parts) < max_parts - 1):
            parts.append(current_items)
            current_items = [(rel_path, block)]
            used = len(block)
        else:
            current_items.append((rel_path, block))
            used += len(block)

    if current_items:
        parts.append(current_items)

    return parts


def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / "fortnite_video_software"

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)

    ignored_dirs = {
        '.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools',
        'compile', 'compiled', 'old_code', 'artifacts', 'packages', 'testresults',
        'venv', '.venv', 'env', '.pytest_cache', '__pycache__'
    }

    divider = "=" * 80

    tree_file = download_dir / "00_file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        tf.write(f"Directory Tree of: {project_root}\n")
        tf.write(f"{divider}\n")
        try:
            for root, dirs, files in os.walk(project_root):
                dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
                rel = Path(root).relative_to(project_root)
                level = len(rel.parts) if rel.name else 0
                indent = ' ' * 4 * level
                folder_name = rel.name if rel.name else project_root.name
                tf.write(f"{indent}{folder_name}/\n")
                subindent = ' ' * 4 * (level + 1)
                for f in sorted(files, key=str.lower):
                    tf.write(f"{subindent}{f}\n")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.bat', '.ps1', '.py', '.json', '.json5', '.xml',
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.ruleset'
    }

    print(f"Aggregating grouped code into {download_dir}...")

    processed_count = 0
    groups = {}

    for old_file in download_dir.glob("*.txt"):
        if old_file.name != "00_file_structure.txt":
            old_file.unlink(missing_ok=True)

    for root, dirs, files in os.walk(project_root):
        dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
        current_path = Path(root)

        for filename in sorted(files, key=str.lower):
            file_path = current_path / filename
            relative_path = file_path.relative_to(project_root)

            if file_path.suffix.lower() not in source_whitelist and file_path.name.lower() not in source_whitelist:
                continue

            if is_binary(file_path):
                content_text = "[BINARY FILE OMITTED - filename is listed in 00_file_structure.txt]\n\n"
            else:
                try:
                    with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                        content_text = f.read()
                    if not content_text.endswith("\n"):
                        content_text += "\n"
                    content_text += "\n"
                except Exception as e:
                    content_text = f"[ERROR READING FILE: {e}]\n\n"

            line_count = content_text.count("\n")
            size_kb = len(content_text) / 1024.0
            header = f"{divider}\nFILE: {relative_path}  ({line_count:,} lines, {size_kb:,.1f} KB)\n{divider}\n"
            block = header + content_text
            groups.setdefault(get_group_name(file_path, project_root), []).append((relative_path, block))
            processed_count += 1

    all_blocks = []
    for group in sorted(groups):
        for item in groups[group]:
            all_blocks.append(item)

    parts = pack_into_capped_parts(all_blocks, max_parts=MAX_CODE_FILES)
    count = len(parts)

    report_rows = []
    if tree_file.exists():
        report_rows.append((tree_file.name, tree_file.stat().st_size))

    for i, part_items in enumerate(parts, start=1):
        part_name = f"code_bundle_part{i:02d}of{count:02d}.txt" if count > 1 else "code_bundle.txt"
        toc = "\n".join(f"  - {rel_path}" for rel_path, _ in part_items)
        banner = (
            f"{divider}\n"
            f"CODE EXPORT BUNDLE - PART {i} OF {count}\n"
            f"Directory map: see 00_file_structure.txt\n"
            f"Files in this bundle ({len(part_items)}):\n"
            f"{toc}\n"
            f"{divider}\n\n"
        )
        part_body = "".join(block for _, block in part_items)
        full_payload = banner + part_body
        with open(download_dir / part_name, "w", encoding="utf-8", errors="replace") as out:
            out.write(full_payload)
        report_rows.append((part_name, len(full_payload.encode('utf-8'))))

    print()
    print("UPLOAD REPORT - upload 00_file_structure.txt and all bundle parts together")
    print("-" * 80)
    grand_total = 0
    for name, byte_size in report_rows:
        grand_total += byte_size
        print(f"  {name:<38} {byte_size / 1024.0:>10,.1f} KB   ~{byte_size // 4:>9,} tokens")
    print("-" * 80)
    print(f"  TOTAL: {len(report_rows)} files (<= 10 limit respected), {grand_total / 1024.0:,.1f} KB")
    print()
    print(f"Done! Aggregated {processed_count} files into {len(parts)} bundle(s) + 1 structure file.")

def purge_bytecode_cache():
    """
    Removes any __pycache__ / .pyc this tool's folder has accumulated.

    ⚠️ THIS IS DELIBERATELY DUPLICATED IN EVERY developer_tools SCRIPT INSTEAD OF BEING SHARED.
    Putting it in a common module would mean each script has to IMPORT that module — and importing
    a local module is the single most reliable way to create the __pycache__ this function exists
    to prevent. A shared helper here would cause the problem it is meant to solve.

    Scoped to developer_tools ONLY. It must never wander into the rest of the repository.
    """
    tools_dir = Path(__file__).resolve().parent
    removed = []
    try:
        for cache_dir in tools_dir.rglob("__pycache__"):
            if cache_dir.is_dir():
                shutil.rmtree(cache_dir, ignore_errors=True)
                if not cache_dir.exists():
                    removed.append(cache_dir.name)
        for stray in list(tools_dir.rglob("*.pyc")) + list(tools_dir.rglob("*.pyo")):
            try:
                stray.unlink()
                removed.append(stray.name)
            except Exception:
                pass
    except Exception as exc:
        print(f"[!] Could not sweep bytecode cache: {exc}")
        return
    if removed:
        print(f"[*] Removed {len(removed)} bytecode cache item(s).")


if __name__ == '__main__':
    purge_bytecode_cache()
    run_aggregator()