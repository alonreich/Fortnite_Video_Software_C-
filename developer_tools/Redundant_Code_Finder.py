import sys
import os
import re
from collections import defaultdict
from pathlib import Path

sys.dont_write_bytecode = True

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

WORKING_DIRECTORY = Path(__file__).resolve().parent.parent
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

try:
    os.chdir(WORKING_DIRECTORY)
except Exception as e:
    print(f"Failed to change working directory: {e}")
    sys.exit(1)

EXCLUDE_FOLDERS = ['.git', 'bin', 'obj', '.vs', 'packages', 'compile', 'compiled', 'old_code']
EXCLUDE_FILES = ['AssemblyInfo.cs']
EXCLUDE_EXTS = ['.txt', '.log', '.json', '.resx', '.ico', '.png', '.gif', '.traineddata', '.dll', '.exe', '.config', '.manifest', '.xml', '.xsd', '.sln', '.DotSettings', '.props', '.targets']

KEYWORDS = set([
    "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
    "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
    "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
    "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
    "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
    "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed",
    "short", "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw",
    "true", "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using",
    "virtual", "void", "volatile", "while", "var", "Task", "get", "set", "yield", "partial", "record"
])

# To filter out data structures and only capture logic, we require at least one of these tokens in the matched block
LOGIC_TOKENS = set(["if", "else", "for", "foreach", "while", "switch", "return", "var", "catch", "=", "+", "-", "*", "/", "==", "!=", "<", ">"])

def get_target_files(root_dir):
    targets = []
    for root, dirs, files in os.walk(root_dir):
        dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_FOLDERS]
        for file in files:
            if file in EXCLUDE_FILES: continue
            _, ext = os.path.splitext(file)
            if ext in EXCLUDE_EXTS: continue
            if ext == '.cs':
                targets.append(os.path.join(root, file))
    return targets

def display_path(filepath):
    try:
        return str(Path(filepath).resolve().relative_to(WORKING_DIRECTORY))
    except Exception:
        return str(filepath)

def remove_comments(source):
    # Remove block comments
    source = re.sub(r'/\*.*?\*/', '', source, flags=re.DOTALL)
    # Remove inline comments
    source = re.sub(r'//.*', '', source)
    return source

def normalize_line(line):
    # Abstract strings
    line = re.sub(r'".*?"', '""', line)
    # Abstract chars
    line = re.sub(r"'.*?'", "''", line)
    
    # Tokenize words, numbers, and symbols
    words = re.findall(r'[a-zA-Z_]\w*|\d+|[^a-zA-Z_\d\s]', line)
    norm = []
    for w in words:
        if w.isdigit():
            norm.append("0")
        elif re.match(r'^[a-zA-Z_]\w*$', w):
            if w in KEYWORDS:
                norm.append(w)
            else:
                norm.append("V")
        else:
            norm.append(w)
    return "".join(norm)

def block_has_logic(norm_lines):
    full_text = "".join(norm_lines)
    # Check if any logic token exists in the normalized block text
    for t in LOGIC_TOKENS:
        if t in full_text:
            # Prevent false positive where '=' is matched but it's just '=>'
            if t == "=" and full_text.count("=") == full_text.count("=>") and full_text.count("==") == 0 and full_text.count("!=") == 0 and full_text.count("<=") == 0 and full_text.count(">=") == 0 and full_text.count("+=") == 0 and full_text.count("-=") == 0:
                continue
            return True
    return False

def generate_html_report(matches, output_path):
    html_parts = []
    html_parts.append("""<!DOCTYPE html>
<html>
<head>
    <title>Redundant Code Report</title>
    <style>
        body { font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif; background: #1e1e1e; color: #d4d4d4; padding: 20px; }
        h1 { color: #569cd6; text-align: center; }
        .match-card { background: #252526; border: 1px solid #3e3e42; border-radius: 8px; margin-bottom: 30px; padding: 15px; box-shadow: 0 4px 6px rgba(0,0,0,0.3); }
        .match-header { font-size: 1.1em; margin-bottom: 15px; color: #4ec9b0; border-bottom: 1px solid #3e3e42; padding-bottom: 10px; }
        .flex-container { display: flex; gap: 20px; }
        .code-col { flex: 1; background: #1e1e1e; border: 1px solid #3e3e42; border-radius: 4px; overflow: hidden; }
        .file-title { background: #2d2d30; padding: 8px 12px; font-weight: bold; font-size: 0.9em; border-bottom: 1px solid #3e3e42; }
        pre { margin: 0; padding: 12px; overflow-x: auto; font-family: 'Consolas', monospace; font-size: 0.85em; line-height: 1.4; color: #d4d4d4; }
        .stat { color: #ce9178; }
    </style>
</head>
<body>
    <h1>Redundant Code Finder - Analysis Report</h1>
    <p style="text-align:center;">Fuzzy-matched duplicates (ignoring spaces, comments, variable names, strings, and numbers).</p>
""")

    if not matches:
        html_parts.append("<p style='text-align:center; color:#c586c0;'>No significant redundancies found!</p>")

    for idx, match in enumerate(matches):
        html_parts.append(f"""
    <div class="match-card">
        <div class="match-header">
            <strong>Match #{idx+1}</strong> - <span class="stat">{match['len']} Lines Duplicated</span>
        </div>
        <div class="flex-container">
            <div class="code-col">
                <div class="file-title">{match['file1']} (Lines {match['line1_start']} - {match['line1_end']})</div>
                <pre>{match['code1']}</pre>
            </div>
            <div class="code-col">
                <div class="file-title">{match['file2']} (Lines {match['line2_start']} - {match['line2_end']})</div>
                <pre>{match['code2']}</pre>
            </div>
        </div>
    </div>
""")

    html_parts.append("</body></html>")
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("".join(html_parts))
    print(f"\n[+] Report saved to: {output_path}")

def main():
    print("--- REDUNDANT CODE FINDER ---")
    files = get_target_files(WORKING_DIRECTORY)
    print(f"[*] Scanning {len(files)} files...")

    lines_db = []
    
    # 1. Parse all files and normalize lines
    for filepath in files:
        rel_path = display_path(filepath)
        try:
            with open(filepath, 'r', encoding='utf-8-sig') as f:
                content = f.read()
            content = remove_comments(content)
            
            for line_num, line_text in enumerate(content.split('\n'), 1):
                stripped = line_text.strip()
                if not stripped: continue
                
                norm = normalize_line(stripped)
                if not norm: continue
                
                lines_db.append((rel_path, line_num, stripped, norm))
        except Exception as e:
            print(f"Error reading {rel_path}: {e}")

    # 2. Build 6-gram hash map
    print("[*] Hashing code logic blocks...")
    N = 6
    ngram_map = defaultdict(list)
    
    for i in range(len(lines_db) - N + 1):
        if lines_db[i][0] == lines_db[i + N - 1][0]:
            ngram = tuple(lines_db[i + j][3] for j in range(N))
            if block_has_logic(ngram):
                ngram_map[ngram].append(i)

    # 3. Find maximal continuous matches
    print("[*] Finding redundant matches...")
    visited_pairs = set()
    matches = []

    cnt=0
    for ngram, indices in ngram_map.items():
        cnt+=1
        if cnt%100==0: print(f'Processing ngram {cnt}/{len(ngram_map)}')
        if len(indices) < 2 or len(indices) > 100: continue
        
        for i in range(len(indices)):
            for j in range(i + 1, len(indices)):
                idx1 = indices[i]
                idx2 = indices[j]
                
                if (idx1, idx2) in visited_pairs:
                    continue
                    
                offset = 0
                collision = False
                while (idx1 + offset < len(lines_db) and 
                       idx2 + offset < len(lines_db) and 
                       lines_db[idx1 + offset][0] == lines_db[idx1][0] and
                       lines_db[idx2 + offset][0] == lines_db[idx2][0] and
                       lines_db[idx1 + offset][3] == lines_db[idx2 + offset][3]):
                    
                    # Prevent self-overlapping in the same file
                    if lines_db[idx1][0] == lines_db[idx2][0] and (idx1 + offset >= idx2):
                        collision = True
                        break
                        
                    offset += 1
                    
                match_len = offset
                
                if match_len < N or collision:
                    continue
                
                # Check for structural complexity to avoid false positives on arrays/braces
                norm_block = [lines_db[idx1 + k][3] for k in range(match_len)]
                if block_has_logic(norm_block):
                    for k in range(match_len):
                        visited_pairs.add((idx1 + k, idx2 + k))
                        visited_pairs.add((idx2 + k, idx1 + k))
                    
                    block1 = lines_db[idx1:idx1+match_len]
                    block2 = lines_db[idx2:idx2+match_len]
                    
                    matches.append({
                        'len': match_len,
                        'file1': block1[0][0],
                        'line1_start': block1[0][1],
                        'line1_end': block1[-1][1],
                        'file2': block2[0][0],
                        'line2_start': block2[0][1],
                        'line2_end': block2[-1][1],
                        'code1': "\n".join(x[2] for x in block1),
                        'code2': "\n".join(x[2] for x in block2)
                    })

    # 4. Sort and output
    print(f'Sorting {len(matches)} matches...')
    matches.sort(key=lambda x: x['len'], reverse=True)
    
    # Output ALL valid logic matches instead of capping
    output_html = str(Path(WORKING_DIRECTORY) / "developer_tools" / "redundant_code_report.html")
    generate_html_report(matches, output_html)
    print(f"[*] Analysis Complete. Found {len(matches)} redundant blocks.")

if __name__ == "__main__":
    main()
