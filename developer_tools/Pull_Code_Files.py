import os
from pathlib import Path

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

def is_binary(file_path):
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

    download_dir.mkdir(parents=True, exist_ok=True)
    initialized_outputs = set()

    ignored_dirs = {'.git', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools', 'compile', 'compiled', 'Old_Code'}
    
    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.py', '.json', '.xml', 
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.ico', '.png', '.jpg', '.jpeg', '.dll', '.exe', '.traineddata'
    }
    
    for root, dirs, files in os.walk(project_root):
        dirs[:] = [d for d in dirs if d not in ignored_dirs]
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
            
            if file_path.suffix.lower() not in source_whitelist:
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
