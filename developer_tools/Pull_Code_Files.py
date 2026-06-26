import os
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
            if not chunk: return False
            if b'\x00' in chunk: return True
            
            try:
                chunk.decode('utf-8')
                return False
            except UnicodeDecodeError:
                return True
    except Exception:
        return True

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    src_dir = project_root / "src"
    download_dir = get_downloads_directory() / "fortnite_video_software"

    if download_dir.exists():
        shutil.rmtree(download_dir)
    download_dir.mkdir(parents=True, exist_ok=True)
    
    tree_file = download_dir / "file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        try:
            res_cd = subprocess.run(["cmd", "/c", "cd"], capture_output=True, text=True, cwd=project_root)
            res_dir = subprocess.run(["cmd", "/c", "dir", "/S", "/A"], capture_output=True, text=True, cwd=project_root)
            tf.write(f"C:\\> cd\n{res_cd.stdout}\nC:\\> dir /S /A\n{res_dir.stdout}")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    initialized_outputs: set[Path] = set()

    ignored_dirs = {'.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools', 'compile', 'compiled', 'old_code'}
    
    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.py', '.json', '.json5', '.xml', 
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.ico', '.png', '.jpg', '.jpeg', '.dll', '.exe', '.traineddata',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.editorconfig', '.ruleset'
    }
    
    for root, dirs, files in os.walk(project_root):
        dirs[:] = [d for d in dirs if d.lower() not in ignored_dirs]
        current_path = Path(root)
        
        if current_path == project_root:
            group_name = "root"
        elif current_path == src_dir:
            group_name = "src"
        elif src_dir in current_path.parents:
            group_name = current_path.relative_to(src_dir).parts[0]
        else:
            group_name = current_path.name

        output_file = download_dir / f"{group_name}.txt"

        for filename in files:
            file_path = current_path / filename
            relative_path = file_path.relative_to(project_root)
            
            if file_path.suffix.lower() not in source_whitelist and file_path.name.lower() not in source_whitelist:
                continue
                
            if is_binary(file_path):
                formatted_entry = f"\n\n\n{relative_path}:\n`[BINARY FILE: {filename} - SKIPPED]\n`"
                mode = 'a' if output_file in initialized_outputs else 'w'
                initialized_outputs.add(output_file)
                with open(output_file, mode, encoding='utf-8') as f_out:
                    f_out.write(formatted_entry)
                continue

            try:
                content = file_path.read_text(encoding='utf-8-sig', errors='ignore')
                formatted_entry = f"\n\n\n{relative_path}:\n`\n{content}\n`"

                mode = 'a' if output_file in initialized_outputs else 'w'
                initialized_outputs.add(output_file)
                with open(output_file, mode, encoding='utf-8') as f_out:
                    f_out.write(formatted_entry)
            except Exception:
                continue

if __name__ == "__main__":
    run_aggregator()
