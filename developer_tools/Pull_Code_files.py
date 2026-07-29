import os
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

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / "fortnite_video_software"

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)
    
    tree_file = download_dir / "00_file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        try:
            res_cd = subprocess.run(["cmd", "/c", "cd"], capture_output=True, text=True, cwd=project_root)
            res_dir = subprocess.run(["cmd", "/c", "dir", "/S", "/A"], capture_output=True, text=True, cwd=project_root)
            tf.write(f"C:\\> cd\n{res_cd.stdout}\nC:\\> dir /S /A\n{res_dir.stdout}")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    ignored_dirs = {'.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools', 'compile', 'compiled', 'old_code'}

    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.py', '.json', '.json5', '.xml', 
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.ico', '.png', '.jpg', '.jpeg', '.dll', '.exe', '.traineddata',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.editorconfig', '.ruleset'
    }

    print(f"Aggregating grouped code into {download_dir}...")

    processed_count = 0
    out_files = {}
    
    # Remove old aggregated files first
    for old_file in download_dir.glob("*.txt"):
        if old_file.name != "00_file_structure.txt":
            old_file.unlink(missing_ok=True)
            
    try:
        for root, dirs, files in os.walk(project_root):
            dirs[:] = [d for d in dirs if d not in ignored_dirs]
            current_path = Path(root)

            for filename in files:
                file_path = current_path / filename
                relative_path = file_path.relative_to(project_root)

                if file_path.suffix.lower() not in source_whitelist and file_path.name.lower() not in source_whitelist:
                    continue
                
                group = get_group_name(file_path, project_root)
                if group not in out_files:
                    out_files[group] = open(download_dir / f"{group}.txt", "w", encoding="utf-8", errors="replace")
                
                out = out_files[group]
                out.write("=" * 80 + "\n")
                out.write(f"FILE: {relative_path}\n")
                out.write("=" * 80 + "\n")

                if is_binary(file_path):
                    out.write("[BINARY FILE OMITTED]\n\n")
                else:
                    try:
                        with open(file_path, "r", encoding="utf-8") as f:
                            content = f.read()
                        out.write(content)
                        if not content.endswith("\n"):
                            out.write("\n")
                        out.write("\n")
                    except Exception as e:
                        out.write(f"[ERROR READING FILE: {e}]\n\n")
                
                processed_count += 1
    finally:
        for f in out_files.values():
            f.close()
                
    print(f"Done! Aggregated {processed_count} files into {len(out_files)} logical groups.")

if __name__ == '__main__':
    run_aggregator()