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

MAX_PART_CHARS = 90_000  # ~22,500 tokens: the safe size for ONE ChatGPT/Gemini message.


def split_oversized_file(rel_path, block, limit):
    """A single file bigger than one paste-part is cut into fixed-size pieces
    and spread over consecutive parts, wrapped in loud CONTINUED banners so
    the chat AI always knows the file is split and where each piece belongs.
    Cut positions are exact character offsets (a piece may start mid-line)."""
    chunk_limit = limit - 1024  # leave room for the banners below
    # Plain fixed-width slicing: no loop state, deterministic by construction,
    # and the piece sizes always sum back to exactly len(block).
    chunks = [block[i:i + chunk_limit] for i in range(0, len(block), chunk_limit)]

    total = len(chunks)
    wrapped = []
    for i, chunk in enumerate(chunks):
        piece = chunk
        if i > 0:
            piece = (
                "=" * 80 + "\n"
                f"[... CONTINUATION OF: {rel_path} - piece {i + 1} of {total} ...]\n"
                "=" * 80 + "\n"
            ) + piece.lstrip("\n")
        if i < total - 1:
            piece += (
                "=" * 80 + "\n"
                "[... THIS FILE IS TOO BIG FOR ONE MESSAGE - IT CONTINUES IN THE NEXT PART ...]\n"
                "=" * 80 + "\n\n"
            )
        wrapped.append(piece)
    return wrapped


def build_parts(blocks, limit):
    """Fix #1: pack whole-file blocks into parts of at most `limit` characters.
    Parts are only ever cut BETWEEN files - unless a single file alone exceeds
    the limit, in which case split_oversized_file handles it."""
    parts, current, used = [], [], 0

    def flush():
        nonlocal current, used
        if current:
            parts.append("".join(current))
        current, used = [], 0

    for rel_path, block in blocks:
        pieces = [block] if len(block) <= limit else split_oversized_file(rel_path, block, limit)
        for piece in pieces:
            if used and used + len(piece) > limit:
                flush()
            current.append(piece)
            used += len(piece)
    flush()
    return parts


def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / "fortnite_video_software"

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)
    
    ignored_dirs = {'.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools', 'compile', 'compiled', 'old_code', 'artifacts'}  # 'artifacts' = test-run output junk (fix #3)

    tree_file = download_dir / "00_file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        tf.write(f"Directory Tree of: {project_root}\n")
        tf.write("=" * 80 + "\n")
        try:
            for root, dirs, files in os.walk(project_root):
                # Fix #4: sorted walk -> the tree is identical on every run.
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
        # TEXT formats only (fix #3). The binary types (.ico, .png, .jpg, .jpeg,
        # .dll, .exe, .traineddata) were removed on purpose: a chat AI cannot read
        # them, and every binary filename is already listed in 00_file_structure.txt.
        '.cs', '.axaml', '.cmd', '.bat', '.ps1', '.py', '.json', '.json5', '.xml',
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.ruleset'
    }

    print(f"Aggregating grouped code into {download_dir}...")

    processed_count = 0
    groups = {}     # group name -> [(relative_path, file_block_text), ...]
    oversized = []  # single files bigger than one part (auto-split, listed in the report)

    # Remove old aggregated files first (00_file_structure.txt above is freshly written).
    for old_file in download_dir.glob("*.txt"):
        if old_file.name != "00_file_structure.txt":
            old_file.unlink(missing_ok=True)
            
    # ---- PASS 1: read every whitelisted file into in-memory blocks per group ----
    for root, dirs, files in os.walk(project_root):
        # Fix #4: sorted walk -> the output is identical on every run (and this
        # loop's dir filter now lower-cases too, matching the tree loop above).
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
                    # errors='replace': never lose a file over one odd character.
                    with open(file_path, "r", encoding="utf-8", errors="replace") as f:
                        content_text = f.read()
                    if not content_text.endswith("\n"):
                        content_text += "\n"
                    content_text += "\n"
                except Exception as e:
                    content_text = f"[ERROR READING FILE: {e}]\n\n"

            # Fix #6: every file header carries its line count and size, so a
            # truncated paste is instantly visible to both human and AI.
            line_count = content_text.count("\n")
            size_kb = len(content_text) / 1024.0
            header = (
                "=" * 80 + "\n"
                f"FILE: {relative_path}  ({line_count:,} lines, {size_kb:,.1f} KB)\n"
                "=" * 80 + "\n"
            )
            block = header + content_text
            if len(block) > MAX_PART_CHARS:
                oversized.append((relative_path, len(block)))

            groups.setdefault(get_group_name(file_path, project_root), []).append((relative_path, block))
            processed_count += 1

    # ---- PASS 2: write each group as one or more chat-message-sized part files ----
    report_rows = []
    if tree_file.exists():
        report_rows.append((tree_file.name, tree_file.stat().st_size))

    total_parts = 0
    for group in sorted(groups):
        parts = build_parts(groups[group], MAX_PART_CHARS)
        count = len(parts)
        for i, part_text in enumerate(parts, start=1):
            part_name = f"{group}_part{i:02d}of{count:02d}.txt" if count > 1 else f"{group}.txt"
            banner = (
                "=" * 80 + "\n"
                "FORTNITE VIDEO SOFTWARE - CODE EXPORT\n"
                f"Group {group} - PART {i} OF {count}\n"
                "Paste 00_file_structure.txt first, then these parts in order,\n"
                "one part per chat message.\n"
                "=" * 80 + "\n\n"
            )
            with open(download_dir / part_name, "w", encoding="utf-8", errors="replace") as out:
                out.write(banner + part_text)
            # Self-check (#1): no part may ever exceed the paste budget.
            if len(banner) + len(part_text) > MAX_PART_CHARS + 2048:
                print(f"  [!] WARNING: {part_name} is {len(banner) + len(part_text):,} chars - OVER the paste limit!")
            report_rows.append((part_name, len(banner) + len(part_text)))
            total_parts += 1

    # ---- Fix #2: PASTE REPORT - know every size BEFORE pasting anything ----
    print()
    print("PASTE REPORT - paste 00 first, then the parts in order, ONE PART PER CHAT MESSAGE")
    print("-" * 80)
    grand_total = 0
    for name, chars in report_rows:
        grand_total += chars
        print(f"  {name:<42} {chars / 1024.0:>10,.1f} KB   ~{chars // 4:>9,} tokens")
    print("-" * 80)
    print(f"  TOTAL: {len(report_rows)} files, {grand_total / 1024.0:,.1f} KB, ~{grand_total // 4:,} tokens")
    if oversized:
        print()
        print("  Single files too big for one message (auto-split with CONTINUED markers):")
        for rel_path, size in oversized:
            print(f"    {rel_path}  ({size / 1024.0:,.1f} KB)")
    print()
    print(f"Done! Aggregated {processed_count} files into {len(groups)} groups / {total_parts} paste-parts.")

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