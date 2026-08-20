import sys
import os
import re
import ctypes
from pathlib import Path
from multiprocessing import Pool, cpu_count

sys.dont_write_bytecode = True

APP_NAME = "KeyPulse"
APP_SLUG = "keypulse"

def get_downloads_directory():
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

WORKING_DIRECTORY = Path(__file__).resolve().parent.parent
TOOL_OUTPUT_DIRECTORY = get_downloads_directory() / APP_SLUG / "developer_tools"
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'
os.environ['PYTHONPYCACHEPREFIX'] = str(TOOL_OUTPUT_DIRECTORY / "pycache")
sys.pycache_prefix = os.environ['PYTHONPYCACHEPREFIX']

try:
    os.chdir(WORKING_DIRECTORY)
except Exception as e:
    print(f"Failed to change working directory: {e}")
    sys.exit(1)

RED = '\033[48;5;52m\033[97;1m'
GREEN = '\033[48;5;22m\033[97m'
CYAN = '\033[96m'
YELLOW = '\033[93m'
RESET = '\033[0m'

def center_console():
    try:
        try: ctypes.windll.shcore.SetProcessDpiAwareness(1)
        except: ctypes.windll.user32.SetProcessDPIAware()
        user32 = ctypes.windll.user32
        hwnd = ctypes.windll.kernel32.GetConsoleWindow()
        if not hwnd: return
        rect = ctypes.Structure()
        user32.GetWindowRect(hwnd, ctypes.byref(rect))
    except: pass

EXCLUDE_FOLDERS = ['.git', '.openai', 'bin', 'obj', '.vs', 'packages', 'compile', 'compiled', 'old_code', 'developer_tools']
EXCLUDE_FILES = ['AssemblyInfo.cs']
EXCLUDE_EXTS = ['.txt', '.log', '.json', '.resx', '.ico', '.png', '.gif', '.traineddata', '.dll', '.exe', '.config', '.manifest', '.xml', '.xsd', '.sln', '.DotSettings', '.props', '.targets']

CS_REGEX = re.compile(r'^(\s*)(?:(?:public|private|protected|internal|static|virtual|override|async|sealed|partial|abstract|readonly|record|unsafe|new)\s+)*(class|struct|interface|enum|delegate|void|int|string|bool|var|Task|auto)\s+([a-zA-Z0-9_<>]+)\s*[\(\{:]?.*$')

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

def is_line_string_safe(line):
    if 'http:' in line or 'https:' in line: return False
    if ':\\' in line or ':/' in line: return False
    return True

def analyze_comments(filepath):
    items = []
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
        
        actions = {}
        in_block_comment = False
        
        for i, line in enumerate(lines):
            stripped = line.strip()
            
            if in_block_comment:
                actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                if '*/' in stripped:
                    in_block_comment = False
                continue
            
            if stripped.startswith('/*'):
                in_block_comment = True
                actions[i] = {'action': 'DELETE', 'type': 'BLOCK COMMENT', 'line': i + 1, 'content': stripped}
                if '*/' in stripped:
                    in_block_comment = False
                continue

            if '//' in line:
                if stripped.startswith('///'):
                    continue
                
                if not is_line_string_safe(line):
                    continue
                
                if stripped.startswith('//'):
                    actions[i] = {'action': 'DELETE', 'type': 'COMMENT', 'line': i + 1, 'content': stripped}
                else:
                    parts = line.split('//', 1)
                    if parts[0].count('"') % 2 == 0:
                        nl = line[len(line.rstrip('\r\n')):]
                        if not nl: nl = '\n'
                        actions[i] = {'action': 'EDIT', 'type': 'INLINE COMMENT', 'line': i + 1, 'content': f"Rem: {parts[1].strip()}", 'new_content': parts[0].rstrip() + nl}

        empty_count = 0
        for i, line in enumerate(lines):
            if i in actions: 
                empty_count = 0
                continue
                
            if not line.strip():
                empty_count += 1
                if empty_count >= 3:
                    actions[i] = {'action': 'DELETE', 'type': 'EXCESSIVE EMPTY', 'line': i + 1, 'content': '<Excessive Empty>'}
            else:
                empty_count = 0

        return [v for k, v in sorted(actions.items())]
    except Exception:
        return []

def nuke_comments(filepath, items):
    try:
        print(f"\n{CYAN}Executing Cleanup: {display_path(filepath)}{RESET}")
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            lines = f.readlines()
        action_map = {item['line'] - 1: item for item in items}
        with open(filepath, 'w', encoding='utf-8-sig') as f:
            for i, line in enumerate(lines):
                if i in action_map:
                    act = action_map[i]
                    print("-" * 60)
                    print(f"Line {act['line']}: {act['type']}")
                    print(f"{RED}- {line.rstrip()}{RESET}")
                    if act['action'] == 'EDIT':
                        print(f"{GREEN}+ {act['new_content'].rstrip()}{RESET}")
                        f.write(act['new_content'])
                else:
                    f.write(line)
        return True
    except Exception as e:
        print(f"Error: {e}")
        return False

def check_syntax(filepath):
    try:
        with open(filepath, 'r', encoding='utf-8-sig') as f:
            source = f.read()
        if '\t' in source: return "Contains Tabs"
        if source.count('{') != source.count('}'): return "Mismatched Braces"
        return None
    except: return None

def analyze_duplicates(filepath):
    found = {}
    duplicates = []
    current_ns, current_cls = "GLOBAL", "GLOBAL"
    try:
        with open(filepath, 'r', encoding='utf-8', errors='ignore') as f:
            for i, line in enumerate(f, 1):
                stripped = line.strip()
                if not stripped or stripped.startswith(('/')): continue
                if stripped.startswith('namespace '):
                    current_ns = stripped.split()[1]
                    continue
                match = CS_REGEX.match(line)
                if match:
                    indent, kw, sig = match.groups()
                    if kw in ['class', 'struct', 'interface']: current_cls = sig
                    key = (current_ns, current_cls, kw, sig)
                    if key not in found: found[key] = []
                    found[key].append(i)
        for (ns, cls, kw, sig), lines in found.items():
            if len(lines) > 1:
                duplicates.append([os.path.basename(filepath), f"{ns}.{cls}", kw, sig, ", ".join(map(str, lines))])
    except: pass
    return duplicates

def print_table(title, data, headers):
    if not data:
        print(f"\n{title}: No issues found.")
        return
    print(f"\n{title}")
    widths = [len(h) for h in headers]
    for row in data:
        for i, val in enumerate(row):
            widths[i] = max(widths[i], len(str(val)))
    widths = [w + 2 for w in widths]
    h_str = " | ".join(f"{h:^{w}}" for h, w in zip(headers, widths))
    print("-" * len(h_str))
    print(h_str)
    print("-" * len(h_str))
    for row in data:
        print(" | ".join(f"{str(val):<{w}}" for val, w in zip(row, widths)))
    print("-" * len(h_str))

def main():
    os.system(f'title {APP_NAME} C# Advanced Code Cleaner')
    print(f"{CYAN}--- {APP_NAME.upper()} C# ADVANCED CODE CLEANER ---{RESET}")
    print(f"Target Directory: {WORKING_DIRECTORY}")
    
    files = get_target_files(WORKING_DIRECTORY)
    print(f"Analyzing {len(files)} files...")
    
    junk_data = []
    files_with_junk = {}
    
    for idx, f in enumerate(files):
        if idx % 20 == 0: print(f"  Scanning for junk... {idx}/{len(files)}", end='\r')
        items = analyze_comments(f)
        if items:
            files_with_junk[f] = items
            for item in items:
                junk_data.append([os.path.basename(f), item['line'], item['type'], (item['content'][:50] + '..') if len(item['content']) > 50 else item['content']])

    print("\n" + "="*80)
    print(f"{YELLOW}STEP 1: REVIEW COMMENTS & UNNECESSARY EMPTY LINES{RESET}")
    print("="*80)
    
    if junk_data:
        print_table("TABLE 1: IDENTIFIED JUNK (COMMENTS & OUTRAGEOUS EMPTY LINES)", junk_data, ["File", "Line", "Type", "Content Preview"])
        print(f"\n{YELLOW}WARNING: This action will permanently remove all items listed above.{RESET}")
        q = input(">>> Do you approve the removal of these comments/empty lines? (Y/N): ").strip().upper()
        if q == 'Y':
            for f, items in files_with_junk.items():
                nuke_comments(f, items)
            print(f"\n{GREEN}Cleanup complete.{RESET}")
        else:
            print(f"\n{CYAN}Cleanup cancelled by user.{RESET}")
    else:
        print("No comments or unnecessary empty lines found.")

    print("\n" + "="*80)
    print(f"{YELLOW}STEP 2: SYSTEM ANALYSIS (SYNTAX & DUPLICATES){RESET}")
    print("="*80)
    
    syntax_data = []
    for f in files:
        err = check_syntax(f)
        if err: syntax_data.append([os.path.basename(f), err])
    print_table("TABLE 2: SYNTAX & INDENTATION WARNINGS", syntax_data, ["File", "Issue"])

    all_dupes = []
    with Pool(processes=cpu_count()) as pool:
        results = pool.map(analyze_duplicates, files)
    for res in results: all_dupes.extend(res)
    print_table("TABLE 3: SCOPE-AWARE DUPLICATES (REPORT ONLY)", all_dupes, ["File", "Scope", "Type", "Signature", "Lines"])

    print(f"\n{CYAN}Done.{RESET}")

if __name__ == "__main__":
    main()
