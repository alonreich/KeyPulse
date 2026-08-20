import os
from pathlib import Path

APP_NAME = "KeyPulse"
APP_SLUG = "keypulse"

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
        if rel_path.endswith(".axaml.cs") or file_path.name in {"MainWindow.axaml.cs", "App.axaml.cs", "SetupWindow.cs"}:
            return "02_UI_Code_Behind"
        if file_path.name in {"HotKeyManager.cs", "InputSimulator.cs", "Models.cs", "Program.cs"}:
            return "03_App_Logic"
        return "04_Other_CSharp"

    if ext in ['.json', '.json5', '.xml', '.csproj', '.sln', '.config', '.props', '.targets', '.ini', '.toml', '.ruleset']:
        return "05_Configuration"

    if ext in ['.ico', '.png', '.jpg', '.jpeg', '.svg', '.dll', '.exe', '.traineddata']:
        return "06_Assets_and_Binaries"

    if ext in ['.py', '.cmd', '.bat', '.ps1', '.md', '.txt', '.yaml', '.yml', '.editorconfig', '.gitignore', '.gitattributes']:
        return "07_Scripts_and_Docs"

    return "08_Misc"

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / APP_SLUG

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)

    ignored_dirs = {'.git', '.openai', 'bin', 'obj', '.vs', '.idea', 'node_modules', 'developer_tools', 'compile', 'compiled', 'old_code'}

    tree_file = download_dir / "00_file_structure.txt"
    with open(tree_file, "w", encoding="utf-8") as tf:
        tf.write(f"Directory Tree of: {project_root}\n")
        tf.write("=" * 80 + "\n")
        try:
            for root, dirs, files in os.walk(project_root):
                dirs[:] = [d for d in dirs if d.lower() not in ignored_dirs]
                rel = Path(root).relative_to(project_root)
                level = len(rel.parts) if rel.name else 0
                indent = ' ' * 4 * level
                folder_name = rel.name if rel.name else project_root.name
                tf.write(f"{indent}{folder_name}/\n")
                subindent = ' ' * 4 * (level + 1)
                for f in files:
                    tf.write(f"{subindent}{f}\n")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.py', '.json', '.json5', '.xml', 
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.ico', '.png', '.jpg', '.jpeg', '.dll', '.exe', '.traineddata',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.editorconfig', '.ruleset'
    }

    print(f"Aggregating {APP_NAME} grouped code into {download_dir}...")

    processed_count = 0
    out_files = {}
    
    # Remove old aggregated files first
    for old_file in download_dir.glob("*.txt"):
        if old_file.name != "00_file_structure.txt":
            old_file.unlink(missing_ok=True)
            
    try:
        for root, dirs, files in os.walk(project_root):
            dirs[:] = [d for d in dirs if d.lower() not in ignored_dirs]
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
