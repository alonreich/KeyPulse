import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO BYTECODE CACHE. BOTH GUARDS BEFORE ANY OTHER IMPORT.
# Prevents stray __pycache__ folders and .pyc files from littering developer_tools.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import shutil
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
    name = file_path.name.lower()

    # 00: Architecture, Specifications, and System Governance (Must be ingested first by AI)
    if rel_path.startswith("docs\\") or rel_path.startswith("docs/") or name in [
        'project_structure.txt', 'readme.md', 'spec_governance.md', 'index.md'
    ]:
        return "00_Specifications_and_Architecture"

    # 01: Core Application Logic, Domain Models & Foundational Services
    if ext == '.cs':
        if name in {
            "program.cs", "hotkeymanager.cs", "inputsimulator.cs", "models.cs",
            "backupservice.cs", "configstore.cs", "cryptohelper.cs",
            "desktoporganizerservice.cs", "updatechecker.cs"
        } or ("service" in name and not rel_path.endswith(".axaml.cs")):
            return "01_Core_Logic_and_Services"

    # 02: Desktop Application UI Code-Behind (Window Controllers & App Lifecycle)
    if ext == '.cs':
        if rel_path.endswith(".axaml.cs") or name in {"mainwindow.axaml.cs", "app.axaml.cs", "setupwindow.cs", "desktoporganizerwindow.axaml.cs"}:
            return "02_UI_Code_Behind"
        return "01_Core_Logic_and_Services"

    # 03: UI Markup & Design Styling (Avalonia AXAML / XAML / CSS)
    if ext in ['.axaml', '.xaml', '.css']:
        return "03_UI_Markup"

    # 04: Project & Build Configuration
    if ext in [
        '.json', '.json5', '.xml', '.csproj', '.sln', '.config', '.props',
        '.targets', '.ini', '.toml', '.ruleset', '.editorconfig', '.manifest'
    ] or name in [
        'app.manifest', '.gitignore', '.gitattributes', '.agignore',
        '.clineignore', '.codexignore', '.geminiignore'
    ]:
        return "04_Configuration"

    # 05: Developer Tools, Automation & Build Harnesses
    if "developer_tools" in rel_path or name in ['build.cmd', 'dev.cmd', 'dev_build.cmd']:
        return "05_Developer_Tools_and_Build"

    # 06: Test Suites & Verification Checks
    if "tests" in rel_path or "test" in rel_path:
        return "06_Tests"

    # 07: Assets & Binary Stubs (Metadata and file size tracking)
    if ext in [
        '.ico', '.png', '.jpg', '.jpeg', '.gif', '.svg', '.dll', '.exe',
        '.traineddata', '.mp3', '.mp4', '.wav', '.bmp', '.webp'
    ]:
        return "07_Assets_and_Binaries"

    return "08_Misc"

MAX_CODE_FILES = 8  # 8 code bundles + 1 manifest + 1 tree file = 10 files max upload limit
MIN_PART_CHARS = 500_000  # Avoid splitting small projects into tiny fragments unnecessarily

def pack_into_capped_parts(items, max_parts=8):
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

def generate_project_manifest(project_root: Path, groups: dict, total_files: int) -> str:
    divider = "=" * 80
    lines = [
        divider,
        f"{APP_NAME.upper()} - ARCHITECTURAL MANIFEST & SYSTEM CONTEXT",
        divider,
        f"Project Root:        {project_root}",
        "Target Framework:    .NET 9 (net9.0-windows)",
        "Architecture Style:  Avalonia UI Desktop (12.1) + Win32 P/Invoke Engine",
        "Publish Profile:     Native AOT Compatible (Single-file NativeAOT, Zero .NET runtime dependencies)",
        "OS Target:           Windows x64 Strictly",
        "",
        "PRIMARY ARCHITECTURAL INVARIANTS (From project_structure.txt):",
        "  1. NativeAOT Compatibility Mandate: Zero reflection. JsonSerializerContext source generators strictly enforced.",
        "  2. Low Mandatory Label SDDL: Single-instance mutex (Local\\KeyPulse_SingleInstance) and exit event use",
        "     SDDL D:(A;;GA;;;WD)S:(ML;;NW;;;LW) to guarantee reliable cross-integrity communication.",
        "  3. Native Asset Unpacking Integrity: Bundled native DLLs extracted to %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime",
        "     verified by SHA-256 content hashes, never by file length.",
        "  4. Non-Destructive Configuration: ConfigStore is the SOLE reader/writer for %APPDATA%\\KeyPulse\\config.json.",
        "     Writes are atomic (.tmp + File.Replace). Damaged files are quarantined (config.damaged-*.json), never deleted.",
        "  5. DPAPI Target Protection: Sensitive shortcut targets protected via DPAPI (ProtectedData, CurrentUser scope).",
        "     Never return ciphertext on decrypt failure.",
        "  6. Atomic Backup / Restore Safety: BackupService enforces format v2 with AES-256-GCM encryption at rest,",
        "     payload SHA-256 verification before deserialization, and pre-restore rollback snapshots.",
        "  7. Foreground Keyboard Hook Guard: Raw capture hook swallows keys ONLY when IsCaptureMode is active AND",
        "     a KeyPulse window is in the foreground.",
        "  8. Win32 Input Injection: Injects hardware scan codes (KEYEVENTF_SCANCODE) via SendInput with InjectionTag.",
        "     Target keyboard layout resolved from foreground window thread via GetTargetKeyboardLayout().",
        "  9. Desktop Organizer Safeguards: Multi-monitor icon coordinates saved atomically. Restoration aborted if Windows",
        "     Auto-Arrange is active. Shell interop strictly non-blocking via SendMessageTimeout.",
        " 10. Elevation Truthfulness & Battery Constraints: Admin scheduled task XML strips battery constraints",
        "     (DisallowStartIfOnBatteries=false). UI reflects effective token elevation, never mere schtasks registration.",
        "",
        "SUBSYSTEM MAP & DIRECTORY HIERARCHY:",
        "  - project_structure.txt:             Sole architectural source of truth, invariants, and system directives.",
        "  - Program.cs & App.axaml.cs:         Boot orchestration, mutex protection, tray icon, and quick action dispatch.",
        "  - HotKeyManager.cs:                  Hidden Win32 message window, RegisterHotKey loop, and key combo probing.",
        "  - InputSimulator.cs:                 Hardware scan-code keyboard injection, layout resolution, clipboard bridge.",
        "  - ConfigStore.cs & Models.cs:        Atomic configuration persistence and strongly-typed data structures.",
        "  - CryptoHelper.cs & BackupService.cs: DPAPI encryption and AES-256-GCM encrypted backup/restore pipeline.",
        "  - DesktopOrganizerService.cs:        Multi-monitor desktop icon snapshotting and coordinate restoration.",
        "  - MainWindow.axaml & .cs:            Primary shortcut grid management, search, and dynamic hotkey capture.",
        "  - SetupWindow.cs:                    Terminal-like installer/uninstaller window bypassing main app loop.",
        "  - Assets\\ & NativeLibs\\:             Sound assets (.wav) and native graphics runtimes (.dll).",
        "  - developer_tools\\:                 Build orchestration, comment cleaning, and AI code extraction.",
        "",
        "EXPORT GROUP METRICS (Ordered by AI ingestion priority):",
    ]
    for group_name in sorted(groups):
        count = len(groups[group_name])
        lines.append(f"  [{group_name}] -> {count:>3} file(s)")
    lines.extend([
        "",
        f"Total Tracked Items: {total_files}",
        "AI INSTRUCTION: Inspect groups in order (00 -> 01 -> 02 -> ...). Enforce architectural invariants during review.",
        divider,
        ""
    ])
    return "\n".join(lines)

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / APP_SLUG

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)

    # Clean up previous text exports to avoid orphaned files
    for old_file in download_dir.glob("*.txt"):
        old_file.unlink(missing_ok=True)

    # developer_tools is deliberately excluded from ignored_dirs so AI has visibility into automation & scripts
    ignored_dirs = {
        '.git', '.openai', 'bin', 'obj', '.vs', '.idea', 'node_modules',
        'compile', 'compiled', 'old_code', 'artifacts', 'packages', 'testresults',
        'venv', '.venv', 'env', '.pytest_cache', '__pycache__'
    }

    divider = "=" * 80

    # 1. Generate annotated directory tree with file sizes
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
                    if f.lower() in {'build.log'}:
                        continue
                    fp = Path(root) / f
                    try:
                        sz = fp.stat().st_size
                        sz_str = f"{sz / (1024 * 1024):.1f} MB" if sz >= 1024 * 1024 else f"{sz / 1024:.1f} KB"
                        tf.write(f"{subindent}{f:<50} [{sz_str:>9}]\n")
                    except Exception:
                        tf.write(f"{subindent}{f}\n")
        except Exception as e:
            tf.write(f"[ERROR GENERATING DIRECTORY TREE: {e}]")

    source_whitelist = {
        '.cs', '.axaml', '.cmd', '.bat', '.ps1', '.py', '.json', '.json5', '.xml',
        '.csproj', '.sln', '.txt', '.md', '.svg', '.manifest', '.config',
        '.props', '.targets', '.editorconfig', '.gitignore', '.gitattributes',
        '.yaml', '.yml', '.razor', '.resx', '.xaml', '.css', '.js', '.ts',
        '.html', '.htm', '.ini', '.toml', '.ruleset',
        '.agignore', '.clineignore', '.codexignore', '.geminiignore'
    }

    binary_whitelist = {
        '.ico', '.png', '.jpg', '.jpeg', '.gif', '.mp3', '.mp4', '.wav',
        '.dll', '.exe', '.traineddata', '.bmp', '.webp'
    }

    print(f"Aggregating {APP_NAME} grouped code into {download_dir}...")

    processed_count = 0
    groups = {}

    for root, dirs, files in os.walk(project_root):
        dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
        current_path = Path(root)

        for filename in sorted(files, key=str.lower):
            if filename.lower() in {'build.log'}:
                continue

            file_path = current_path / filename
            relative_path = file_path.relative_to(project_root)
            ext = file_path.suffix.lower()

            is_src = ext in source_whitelist or file_path.name.lower() in source_whitelist
            is_bin = ext in binary_whitelist

            if not is_src and not is_bin:
                continue

            if is_bin or is_binary(file_path):
                file_size = file_path.stat().st_size
                sz_str = f"{file_size / (1024 * 1024):.2f} MB" if file_size >= 1024 * 1024 else f"{file_size / 1024:.1f} KB"
                content_text = (
                    f"[BINARY / MEDIA ASSET OMITTED FROM TEXT EXPORT]\n"
                    f"Relative Path: {relative_path}\n"
                    f"File Size:     {sz_str} ({file_size:,} bytes)\n"
                    f"Type:          {ext.upper() if ext else 'Binary'} Asset\n"
                    f"Note:          Full path indexed in 00_file_structure.txt. Kept on disk.\n\n"
                )
                line_count = 0
                size_kb = file_size / 1024.0
            else:
                try:
                    with open(file_path, "r", encoding="utf-8-sig", errors="replace") as f:
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
            group_name = get_group_name(file_path, project_root)
            groups.setdefault(group_name, []).append((relative_path, block))
            processed_count += 1

    # 2. Generate and write high-level manifest
    manifest_file = download_dir / "00_PROJECT_MANIFEST.txt"
    manifest_content = generate_project_manifest(project_root, groups, processed_count)
    with open(manifest_file, "w", encoding="utf-8") as mf:
        mf.write(manifest_content)

    # 3. Assemble all blocks following strict group priority order (00 -> 01 -> 02 -> ...)
    all_blocks = []
    for group in sorted(groups):
        for item in groups[group]:
            all_blocks.append(item)

    parts = pack_into_capped_parts(all_blocks, max_parts=MAX_CODE_FILES)
    count = len(parts)

    report_rows = []
    if manifest_file.exists():
        report_rows.append((manifest_file.name, manifest_file.stat().st_size))
    if tree_file.exists():
        report_rows.append((tree_file.name, tree_file.stat().st_size))

    for i, part_items in enumerate(parts, start=1):
        part_name = f"code_bundle_part{i:02d}of{count:02d}.txt" if count > 1 else "code_bundle.txt"
        toc = "\n".join(f"  - {rel_path}" for rel_path, _ in part_items)
        banner = (
            f"{divider}\n"
            f"CODE EXPORT BUNDLE - PART {i} OF {count}\n"
            f"Context Manifest: 00_PROJECT_MANIFEST.txt\n"
            f"Directory Tree:   00_file_structure.txt\n"
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
    print("UPLOAD REPORT - upload 00_PROJECT_MANIFEST.txt, 00_file_structure.txt, and bundle parts together")
    print("-" * 80)
    grand_total = 0
    for name, byte_size in report_rows:
        grand_total += byte_size
        print(f"  {name:<38} {byte_size / 1024.0:>10,.1f} KB   ~{byte_size // 4:>9,} tokens")
    print("-" * 80)
    print(f"  TOTAL: {len(report_rows)} files (<= 10 limit respected), {grand_total / 1024.0:,.1f} KB")
    print()
    print(f"Done! Aggregated {processed_count} files into {len(parts)} bundle(s) + 1 manifest + 1 structure file.")

def purge_bytecode_cache():
    """
    Removes any __pycache__ / .pyc this tool's folder has accumulated.
    Scoped to developer_tools ONLY.
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
