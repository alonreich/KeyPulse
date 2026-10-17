import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO BYTECODE CACHE. BOTH GUARDS BEFORE ANY OTHER IMPORT.
# Prevents stray __pycache__ folders and .pyc files from littering developer_tools.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import shutil
import hashlib
from pathlib import Path
from datetime import datetime, timezone

APP_NAME = "KeyPulse"
APP_SLUG = "keypulse"

# Upload constraint: at most 10 files total (1 manifest + 1 minimap/structure + at most 8 code bundles)
MAX_CODE_FILES = 8
MIN_PART_CHARS = 450_000  # Avoid splitting files into tiny fragments; balances around ~110k-130k tokens/file

def get_downloads_directory() -> Path:
    user_profile = os.environ.get('USERPROFILE')
    if user_profile:
        return Path(user_profile) / "Downloads"
    return Path.home() / "Downloads"

def compute_sha256(file_path: Path) -> str:
    """Calculates SHA-256 hex digest for binary verification."""
    h = hashlib.sha256()
    try:
        with open(file_path, "rb") as f:
            while chunk := f.read(65536):
                h.update(chunk)
        return h.hexdigest()
    except Exception as e:
        return f"[ERROR COMPUTING SHA-256: {e}]"

def is_binary(file_path: Path) -> bool:
    try:
        with open(file_path, 'rb') as f:
            chunk = f.read(2048)
            if b'\0' in chunk:
                return True
    except Exception:
        pass
    return False

# Architectural registry of known binaries: (Category, Role, Integration, Deployment / Runtime Verification)
KNOWN_BINARY_REGISTRY = {
    "av_libglesv2.dll": (
        "Native Runtime Dynamic-Link Library (ANGLE GLESv2)",
        "ANGLE OpenGL ES 2.0 translation library for DirectX. Required by Avalonia UI for hardware-accelerated rendering.",
        '<EmbeddedResource Include="NativeLibs\\av_libglesv2.dll" /> in KeyPulse.csproj',
        "Extracted at boot by Program.ExtractNativeLibs() into %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime. SHA-256 hash verified against embedded resource."
    ),
    "libharfbuzzsharp.dll": (
        "Native Runtime Dynamic-Link Library (HarfBuzz Text Shaping)",
        "HarfBuzz OpenType font shaping engine. Required by Avalonia UI for high-fidelity text rendering and typography.",
        '<EmbeddedResource Include="NativeLibs\\libHarfBuzzSharp.dll" /> in KeyPulse.csproj',
        "Extracted at boot by Program.ExtractNativeLibs() into %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime. SHA-256 hash verified against embedded resource."
    ),
    "libskiasharp.dll": (
        "Native Runtime Dynamic-Link Library (Skia 2D Graphics Engine)",
        "Skia 2D cross-platform vector graphics engine. Required by Avalonia UI for window rendering and 2D canvas drawing.",
        '<EmbeddedResource Include="NativeLibs\\libSkiaSharp.dll" /> in KeyPulse.csproj',
        "Extracted at boot by Program.ExtractNativeLibs() into %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime. SHA-256 hash verified against embedded resource."
    ),
    "click.wav": (
        "Audio Sound Effect (PCM WAV)",
        "Interactive audio feedback sound played when a shortcut or user action is triggered.",
        '<EmbeddedResource Include="Assets\\click.wav" /> in KeyPulse.csproj',
        "Loaded dynamically via SoundHelper/Audio playback service."
    ),
    "close.wav": (
        "Audio Sound Effect (PCM WAV)",
        "Interactive audio feedback sound played on window or overlay dismissal.",
        '<EmbeddedResource Include="Assets\\close.wav" /> in KeyPulse.csproj',
        "Loaded dynamically via SoundHelper/Audio playback service."
    ),
    "error.wav": (
        "Audio Sound Effect (PCM WAV)",
        "Audio feedback sound played on hotkey conflicts, unreachable shortcut targets, or runtime errors.",
        '<EmbeddedResource Include="Assets\\error.wav" /> in KeyPulse.csproj',
        "Loaded dynamically via SoundHelper/Audio playback service."
    ),
    "keypulse.ico": (
        "Windows Application Icon (Multi-Resolution ICO)",
        "Primary application icon embedded into executable PE header, Win32 manifest, window chrome, and system tray notification area.",
        '<ApplicationIcon>KeyPulse.ico</ApplicationIcon> and <AvaloniaResource Include="KeyPulse.ico" /> in KeyPulse.csproj',
        "Embedded at compile time into the binary PE header and Avalonia resource stream."
    )
}

def create_binary_placeholder(file_path: Path, relative_path: Path) -> tuple[str, int, str]:
    ext = file_path.suffix.lower()
    name = file_path.name.lower()
    file_size = file_path.stat().st_size
    sz_str = f"{file_size / (1024 * 1024):.2f} MB" if file_size >= 1024 * 1024 else f"{file_size / 1024:.1f} KB"
    sha256_hash = compute_sha256(file_path)

    info = KNOWN_BINARY_REGISTRY.get(name)
    if info:
        cat, role, integration, deployment = info
    else:
        cat = f"{ext.upper() if ext else 'Binary'} Asset"
        role = "Binary asset tracked in repository."
        integration = f"Referenced at relative path {relative_path}"
        deployment = "Packaged or referenced by application runtime."

    placeholder = (
        f"[BINARY / MEDIA ASSET PLACEHOLDER - OMITTED FROM TEXT EXPORT]\n"
        f"File Path:          {relative_path}\n"
        f"File Size:          {sz_str} ({file_size:,} bytes)\n"
        f"Asset Category:     {cat}\n"
        f"SHA-256 Checksum:   {sha256_hash}\n"
        f"Architectural Role: {role}\n"
        f"Integration Method: {integration}\n"
        f"Deployment Rule:    {deployment}\n"
        f"AI Guidance:        Do not modify this binary directly in text diffs.\n"
        f"                    Binary exists on disk at '{relative_path}' and is preserved by git.\n\n"
    )
    return placeholder, file_size, sha256_hash

def get_group_name(file_path: Path, project_root: Path) -> str:
    ext = file_path.suffix.lower()
    rel_path = str(file_path.relative_to(project_root)).replace('/', '\\').lower()
    name = file_path.name.lower()

    # 00: Architecture, Specifications, and System Governance (Must be ingested first by AI)
    if rel_path.startswith("docs\\") or name in {
        'project_structure.txt', 'readme.md', 'spec_governance.md', 'index.md'
    }:
        return "00_Specifications_and_Architecture"

    # 01 & 02: C# Code classification
    if ext == '.cs':
        # Window controllers & App lifecycle UI
        if rel_path.endswith(".axaml.cs") or name.endswith("window.cs") or name in {"app.axaml.cs"}:
            return "02_UI_Code_Behind"
        # Core services, domain models, helpers, persistence, and entry points
        return "01_Core_Logic_and_Services"

    # 03: UI Markup & Design Styling (Avalonia AXAML / XAML / CSS)
    if ext in ['.axaml', '.xaml', '.css']:
        return "03_UI_Markup"

    # 04: Project & Build Configuration
    if ext in [
        '.json', '.json5', '.xml', '.csproj', '.sln', '.config', '.props',
        '.targets', '.ini', '.toml', '.ruleset', '.editorconfig', '.manifest'
    ] or name in [
        'app.manifest', 'version.txt', '.gitignore', '.gitattributes', '.agignore',
        '.clineignore', '.codexignore', '.geminiignore'
    ]:
        return "04_Configuration"

    # 05: Developer Tools, Automation, CI/CD & Build Harnesses
    if (
        rel_path.startswith("build\\") or
        rel_path.startswith("developer_tools\\") or
        name in ['build.cmd', 'dev.cmd', 'dev_build.cmd', 'pull_code.cmd']
    ):
        return "05_Developer_Tools_and_Build"

    # 06: Test Suites & Verification Checks
    if "tests" in rel_path or "test" in rel_path:
        return "06_Tests"

    # 07: Assets & Binary Stubs (Metadata, hash and file size tracking)
    if ext in [
        '.ico', '.png', '.jpg', '.jpeg', '.gif', '.svg', '.dll', '.exe',
        '.traineddata', '.mp3', '.mp4', '.wav', '.bmp', '.webp'
    ] or rel_path.startswith("assets\\") or rel_path.startswith("nativelibs\\"):
        return "07_Assets_and_Binaries"

    return "08_Misc"

def load_sentinels(project_root: Path) -> list[tuple[str, str]]:
    """Loads regression sentinels from build\\sentinels.txt."""
    sentinels_file = project_root / "build" / "sentinels.txt"
    if not sentinels_file.exists():
        return []
    sentinels = []
    try:
        with open(sentinels_file, "r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line or line.startswith("#"):
                    continue
                if "=" in line:
                    token, target = line.split("=", 1)
                    sentinels.append((token.strip(), target.strip()))
    except Exception:
        pass
    return sentinels

def pack_into_capped_parts(items: list[tuple[str, str]], max_parts: int = MAX_CODE_FILES) -> list[list[tuple[str, str]]]:
    """Packs (rel_path, block) tuples across at most max_parts bundles."""
    if not items:
        return []

    total_chars = sum(len(b) for _, b in items)
    target_limit = max(MIN_PART_CHARS, (total_chars // max_parts) + 1)

    parts: list[list[tuple[str, str]]] = []
    current_items: list[tuple[str, str]] = []
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
    sentinels = load_sentinels(project_root)

    lines = [
        divider,
        f"{APP_NAME.upper()} - ARCHITECTURAL MANIFEST & SYSTEM CONTEXT",
        divider,
        f"Project Root:        {project_root}",
        f"Generated UTC:       {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')} UTC",
        "Target Framework:    .NET 9 (net9.0-windows)",
        "Architecture Style:  Avalonia UI Desktop (12.1) + Win32 P/Invoke Engine",
        "Publish Profile:     Native AOT Compatible (Single-file NativeAOT, Zero .NET runtime dependencies)",
        "OS Target:           Windows x64 Strictly",
        "",
        "PRIMARY ARCHITECTURAL INVARIANTS (Authoritative from project_structure.txt):",
        "   1. NativeAOT Compatibility Mandate: Zero reflection. JsonSerializerContext source generators strictly enforced.",
        "   2. Low Mandatory Label SDDL: Single-instance mutex (Local\\KeyPulse_SingleInstance_Mutex_v1) and exit event use",
        "      SDDL D:(A;;GA;;;WD)S:(ML;;NW;;;LW) to guarantee reliable cross-integrity communication.",
        "   3. Single Startup Authority & Schtasks Battery Stripping: Program.SetStartup/IsStartupEnabled are sole authorities.",
        "      Task Scheduler XML explicitly strips battery limits (DisallowStartIfOnBatteries=false, StopIfGoingOnBatteries=false).",
        "   4. Native Asset Unpacking Integrity: Bundled native DLLs extracted to %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime",
        "      verified by SHA-256 content hashes, never by file length alone.",
        "   5. Non-Destructive Configuration: ConfigStore is the SOLE reader/writer for %APPDATA%\\KeyPulse\\config.json.",
        "      Writes are atomic (.tmp + File.Replace). Damaged files are quarantined (config.damaged-*.json), never deleted.",
        "   6. DPAPI Target Protection: Sensitive shortcut targets protected via DPAPI (ProtectedData, CurrentUser scope).",
        "      Never return ciphertext on decrypt failure.",
        "   7. Atomic Backup / Restore Safety: BackupService enforces format v2 with AES-256-GCM encryption at rest,",
        "      payload SHA-256 verification before deserialization, and pre-restore rollback snapshots.",
        "   8. Foreground Keyboard Hook Guard: Raw capture hook swallows keys ONLY when IsCaptureMode is active AND",
        "      a KeyPulse window is in the foreground (IsKeyPulseForeground()).",
        "   9. Win32 Input Injection: Injects hardware scan codes (KEYEVENTF_SCANCODE) via SendInput with InjectionTag.",
        "      Target keyboard layout resolved from foreground window thread via GetTargetKeyboardLayout().",
        "  10. Clipboard Backup Budget & Owner HWND: 96 MB total budget, dedicated owner HWND (_clipboardOwnerHwnd),",
        "      and GetClipboardSequenceNumber() verification to abort restore if foreign app copied new content.",
        "  11. Screen Brightness Subsystem & Hardware Protection Protocol: Physical DDC/CI via dxva2.dll with",
        "      DestroyPhysicalMonitors in finally blocks. Internal laptop backlight via Win32 kernel DeviceIoControl",
        "      (IOCTL_VIDEO_SET_DISPLAY_BRIGHTNESS). Multi-display updates MUST be sequential with 30ms settling delay",
        "      (no Parallel.ForEach). Dual brightness/contrast writes enforce mandatory 50ms VESA MCCS pause. Hardware",
        "      raw values clamped to [dwMin, dwMax]. 250ms hardware throttle with trailing-edge flush, delta-only writes,",
        "      decoupled disk persistence (zero disk thrashing on slider drag), and ActionType.CycleBrightness cycling.",
        "  12. Desktop Organizer Safeguards: Multi-monitor icon coordinates saved atomically. Restoration aborted if Windows",
        "      Auto-Arrange is active. Corrupt configs quarantined; LKG backup restored. Non-blocking SendMessageTimeout.",
        "  13. Pre-Wipe Safe Transaction: ConfigStore.SaveAllWipeSafetyCopies() writes safety copies outside settings dir",
        "      before any configuration wipe.",
        "  14. Developer Sandbox Isolation (DEVDATA_01): When KEYPULSE_DEV_MODE=1 / run-ui, state routes exclusively to",
        "      %LOCALAPPDATA%\\KeyPulse_DEV, guaranteeing zero cross-contamination with production %APPDATA%\\KeyPulse.",
        "  15. Repo-Scoped Process Termination (BUILDKILL_01): dev.cmd and BuildPipeline.ps1 inspect process executable path",
        "      and terminate ONLY processes located inside this repository, leaving user's installed instance untouched.",
        "  16. Named Pipe Shell Integration (IpcService): Low-integrity SDDL named pipe for shell context-menu requests.",
        "",
        "CRITICAL FIX SENTINELS (Verified by build\\VerifySentinels.ps1 before compilation):",
    ]

    if sentinels:
        for token, target in sentinels:
            lines.append(f"  - [{target:<32}] Must contain sentinel: '{token}'")
    else:
        lines.append("  (No sentinels found in build\\sentinels.txt)")

    lines.extend([
        "",
        "ACTIVE SYSTEM PATHS:",
        "  - Installation:        %LOCALAPPDATA%\\Programs\\KeyPulse",
        "  - Main Binary:         %LOCALAPPDATA%\\Programs\\KeyPulse\\KeyPulse.exe",
        "  - Native Runtimes:     %LOCALAPPDATA%\\Programs\\KeyPulse\\runtime  (SHA-256 verified)",
        "  - Configuration:       %APPDATA%\\KeyPulse\\config.json",
        "  - Desktop Organizer:   %APPDATA%\\KeyPulse\\desktop_organizer.json",
        "  - Screen Brightness:   %APPDATA%\\KeyPulse\\screen_brightness.json",
        "  - Diagnostic Logs:     %APPDATA%\\KeyPulse\\logs\\keypulse_debug.txt",
        "  - Dev Sandbox:         %LOCALAPPDATA%\\KeyPulse_DEV  (when KEYPULSE_DEV_MODE=1)",
        "",
        "EXPORT GROUP METRICS (Ordered by AI ingestion priority):",
    ])

    for group_name in sorted(groups):
        count = len(groups[group_name])
        lines.append(f"  [{group_name}] -> {count:>3} file(s)")

    lines.extend([
        "",
        f"Total Tracked Files: {total_files}",
        "AI INGESTION & EDITING DIRECTIVES:",
        "  1. Read 00_file_structure.txt first for the Master Bundle Cross-Reference Minimap.",
        "  2. Ingest code bundles in order (Part 01 -> Part 02 -> ...).",
        "  3. Do NOT remove or modify any fix sentinel tokens listed above.",
        "  4. Do NOT introduce reflection (NativeAOT requirement). Use source generators for JSON/regex.",
        "  5. Do NOT modify binary assets directly; use placeholders for SHA-256 and metadata verification.",
        divider,
        ""
    ])
    return "\n".join(lines)

def generate_intelligent_minimap(
    project_root: Path,
    file_bundle_map: dict[str, tuple[str, int, float, str]],
    binary_registry_data: list[tuple[str, int, str, str, str]],
    tree_lines: list[str]
) -> str:
    """Generates 00_file_structure.txt containing the intelligent minimap, bundle cross-reference, and directory tree."""
    divider = "=" * 80
    subdivider = "-" * 80

    lines = [
        divider,
        f"{APP_NAME.upper()} - INTELLIGENT PROJECT MINIMAP & FILE LOCATOR",
        divider,
        f"Project Root:        {project_root}",
        f"Generated UTC:       {datetime.now(timezone.utc).strftime('%Y-%m-%d %H:%M:%S')} UTC",
        "Context Manifest:    00_PROJECT_MANIFEST.txt",
        "",
        "HOW TO NAVIGATE THIS EXPORT AS AN AI MODEL:",
        "  1. Use the 'BUNDLE CROSS-REFERENCE MINIMAP' below to instantly locate which uploaded bundle",
        "     file contains any specific source code file.",
        "  2. Use the 'SUBSYSTEM & FUNCTIONAL DIRECTORY' to understand architectural component mapping.",
        "  3. Use the 'BINARY & NATIVE ASSET REGISTRY' to inspect cryptographic hashes and integration methods",
        "     for non-text dependencies without needing raw binary bytes.",
        "  4. Use the 'HIERARCHICAL REPOSITORY DIRECTORY TREE' for complete folder layout and file sizes.",
        "",
        divider,
        "SECTION 1: BUNDLE CROSS-REFERENCE MINIMAP (File -> Upload Bundle Location)",
        divider,
        f"{'File Relative Path':<42} | {'Target Bundle File':<28} | {'Lines':>6} | {'Size':>9} | {'Category'}",
        subdivider
    ]

    for rel_path in sorted(file_bundle_map.keys(), key=str.lower):
        part_name, line_cnt, size_kb, cat = file_bundle_map[rel_path]
        sz_str = f"{size_kb:,.1f} KB" if size_kb < 1024 else f"{size_kb / 1024:,.2f} MB"
        clean_cat = cat.replace("00_", "").replace("01_", "").replace("02_", "").replace("03_", "").replace("04_", "").replace("05_", "").replace("07_", "")
        lines.append(f"{rel_path:<42} | {part_name:<28} | {line_cnt:>6,} | {sz_str:>9} | {clean_cat}")

    lines.extend([
        "",
        divider,
        "SECTION 2: SUBSYSTEM & FUNCTIONAL DIRECTORY",
        divider,
        "1. SPECIFICATIONS & SYSTEM GOVERNANCE:",
        "   - project_structure.txt              Sole architectural source of truth, invariants, and directives.",
        "   - README.md                          High-level repository and project overview.",
        "",
        "2. APPLICATION LIFECYCLE & INPUT AUTOMATION:",
        "   - Program.cs                         Startup arguments, single-instance mutex, admin task, DLL extraction.",
        "   - HotKeyManager.cs                   Hidden Win32 message window, RegisterHotKey loop, key probing.",
        "   - InputSimulator.cs                  Hardware scan-code SendInput, target layout resolution, clipboard bridge.",
        "   - App.axaml / App.axaml.cs           Avalonia application lifecycle, tray icon menu, quick action dispatcher.",
        "",
        "3. DESKTOP USER INTERFACE (WINDOWS & MARKUP):",
        "   - MainWindow.axaml / .cs             Primary shortcut grid, search filter, and dynamic hotkey editor.",
        "   - DesktopOrganizerWindow.axaml / .cs Multi-monitor desktop icon snapshot profiles and placement dialog.",
        "   - ScreenBrightnessWindow.cs          Full configuration window for multi-display hardware brightness/contrast.",
        "   - ScreenBrightnessOverlayWindow.cs   Borderless auto-closing fast tray overlay with dual vertical sliders.",
        "   - SetupWindow.cs                     Terminal-like installer/uninstaller window bypassing main app loop.",
        "",
        "4. HARDWARE SERVICES & SYSTEM PERSISTENCE:",
        "   - DesktopOrganizerService.cs         Icon coordinates capture/restore, LKG backups, and periodic snapshots.",
        "   - ScreenBrightnessService.cs         DDC/CI dxva2.dll bus interop, laptop DeviceIoControl, 250ms throttle.",
        "   - ConfigStore.cs & Models.cs         Atomic JSON persistence, data models, and pre-wipe safety copies.",
        "   - BackupService.cs & CryptoHelper.cs Encrypted backup v2 (AES-GCM), DPAPI target protection.",
        "   - AtomicFileWriter.cs                Atomic multi-attempt Win32 File.Replace with staging fallback.",
        "   - IpcService.cs                      Named Pipe low-integrity IPC for Windows Explorer context-menu add.",
        "   - UpdateChecker.cs                   GitHub Releases API checker and local dev binary update source.",
        "",
        "5. BUILD PIPELINE, SENTINELS & DEVELOPER TOOLS:",
        "   - build.cmd / build\\BuildPipeline.ps1 Official release pipeline, git tagging, and GitHub drafts.",
        "   - dev.cmd / build\\DevSandbox.ps1     Interactive watch mode in isolated %LOCALAPPDATA%\\KeyPulse_DEV sandbox.",
        "   - dev_build.cmd                      Local NativeAOT test compile (0.0.0-dev timestamp) without git commits.",
        "   - build\\sentinels.txt & VerifySentinels.ps1 Sentinel verification preventing regression of 20 critical fixes.",
        "   - developer_tools\\BuildLog.ps1       Live colored console build logger and summary reporter.",
        "   - developer_tools\\Pull_Code_files.py Code staging and upload packager for cloud AI models.",
        "   - developer_tools\\Redundant_Code_Finder.py AST/token fuzzy duplicate code detector.",
        "   - developer_tools\\Manual_UnInstall.bat Standalone complete system cleaner for test machines.",
        "",
        "6. ASSETS & EMBEDDED DEPENDENCIES:",
        "   - NativeLibs\\*.dll                   HarfBuzz, SkiaSharp, and ANGLE GLESv2 runtime libraries.",
        "   - Assets\\*.wav                      Click, close, and error sound effects.",
        "   - KeyPulse.ico                       Multi-resolution Windows application icon.",
        "",
        divider,
        "SECTION 3: BINARY & NATIVE ASSET REGISTRY",
        divider,
        f"{'Relative File Path':<32} | {'File Size':>10} | {'SHA-256 Checksum':<64}",
        subdivider
    ])

    for rel_path, file_sz, sha256_val, cat, role in binary_registry_data:
        sz_str = f"{file_sz / (1024 * 1024):.2f} MB" if file_sz >= 1024 * 1024 else f"{file_sz / 1024:.1f} KB"
        lines.append(f"{rel_path:<32} | {sz_str:>10} | {sha256_val}")
        lines.append(f"  -> [{cat}] {role}")

    lines.extend([
        "",
        divider,
        "SECTION 4: HIERARCHICAL REPOSITORY DIRECTORY TREE",
        divider
    ])
    lines.extend(tree_lines)
    lines.append("")

    return "\n".join(lines)

def run_aggregator():
    project_root = Path(__file__).resolve().parent.parent
    download_dir = get_downloads_directory() / APP_SLUG

    if not download_dir.exists():
        download_dir.mkdir(parents=True, exist_ok=True)

    # Clean up previous text exports to avoid orphaned bundle files
    for old_file in download_dir.glob("*.txt"):
        try:
            old_file.unlink(missing_ok=True)
        except Exception:
            pass

    ignored_dirs = {
        '.git', '.openai', 'bin', 'obj', '.vs', '.idea', 'node_modules',
        'compile', 'compiled', 'old_code', 'artifacts', 'packages', 'testresults',
        'venv', '.venv', 'env', '.pytest_cache', '__pycache__', '.devlogs'
    }

    ignored_files = {
        'build.log', 'build.result.txt', '.ds_store',
        'desktop_organizer.lkg.json', 'screen_brightness.lkg.json'
    }

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

    divider = "=" * 80
    print(f"Aggregating {APP_NAME} codebase into {download_dir}...")

    # 1. First pass: Collect all files and build directory tree representation
    tree_lines = [f"{project_root.name}/"]
    processed_count = 0
    groups: dict[str, list[tuple[str, str, int, float]]] = {}
    binary_registry_data: list[tuple[str, int, str, str, str]] = []

    for root, dirs, files in os.walk(project_root):
        dirs[:] = sorted((d for d in dirs if d.lower() not in ignored_dirs), key=str.lower)
        current_path = Path(root)
        rel_root = current_path.relative_to(project_root)
        level = len(rel_root.parts) if rel_root.name else 0

        if rel_root.name:
            indent = ' ' * 4 * level
            tree_lines.append(f"{indent}{rel_root.name}/")

        subindent = ' ' * 4 * (level + 1)

        for filename in sorted(files, key=str.lower):
            if filename.lower() in ignored_files or filename.endswith('.tmp'):
                continue

            file_path = current_path / filename
            relative_path = file_path.relative_to(project_root)
            rel_str = str(relative_path).replace('/', '\\')
            ext = file_path.suffix.lower()

            is_src = ext in source_whitelist or file_path.name.lower() in source_whitelist
            is_bin = ext in binary_whitelist or is_binary(file_path)

            if not is_src and not is_bin:
                continue

            file_stat = file_path.stat()
            file_size = file_stat.st_size
            sz_str = f"{file_size / (1024 * 1024):.2f} MB" if file_size >= 1024 * 1024 else f"{file_size / 1024:.1f} KB"

            if is_bin:
                placeholder_text, fsize, sha256_val = create_binary_placeholder(file_path, relative_path)
                line_count = 0
                size_kb = fsize / 1024.0
                content_text = placeholder_text
                tree_lines.append(f"{subindent}{filename:<46} [{sz_str:>9}] (Binary)")
                
                info = KNOWN_BINARY_REGISTRY.get(filename.lower())
                cat_desc = info[0] if info else f"{ext.upper()} Asset"
                role_desc = info[1] if info else "Binary asset tracked in repository."
                binary_registry_data.append((rel_str, file_size, sha256_val, cat_desc, role_desc))
            else:
                try:
                    with open(file_path, "r", encoding="utf-8-sig", errors="replace") as f:
                        content_text = f.read()
                    if not content_text.endswith("\n"):
                        content_text += "\n"
                except Exception as e:
                    content_text = f"[ERROR READING FILE: {e}]\n"

                line_count = content_text.count("\n")
                size_kb = len(content_text.encode('utf-8')) / 1024.0
                tree_lines.append(f"{subindent}{filename:<46} [{sz_str:>9}] ({line_count:,} lines)")

            group_name = get_group_name(file_path, project_root)
            header = (
                f"{divider}\n"
                f"START OF FILE: {rel_str}  ({line_count:,} lines, {size_kb:,.1f} KB) [Group: {group_name}]\n"
                f"{divider}\n"
            )
            footer = (
                f"\n{divider}\n"
                f"END OF FILE: {rel_str}\n"
                f"{divider}\n\n"
            )
            block = header + content_text + footer

            groups.setdefault(group_name, []).append((rel_str, block, line_count, size_kb))
            processed_count += 1

    # 2. Assemble all blocks following strict priority order (00 -> 01 -> 02 -> ...)
    all_blocks: list[tuple[str, str]] = []
    item_metadata: dict[str, tuple[int, float, str]] = {}

    for group in sorted(groups):
        for rel_str, block, line_count, size_kb in groups[group]:
            all_blocks.append((rel_str, block))
            item_metadata[rel_str] = (line_count, size_kb, group)

    # 3. Pack blocks into at most MAX_CODE_FILES (8) bundles
    parts = pack_into_capped_parts(all_blocks, max_parts=MAX_CODE_FILES)
    count = len(parts)

    # 4. Map each file to its assigned bundle file name
    file_bundle_map: dict[str, tuple[str, int, float, str]] = {}
    for i, part_items in enumerate(parts, start=1):
        part_name = f"code_bundle_part{i:02d}of{count:02d}.txt" if count > 1 else "code_bundle.txt"
        for rel_str, _ in part_items:
            line_cnt, sz_kb, grp = item_metadata[rel_str]
            file_bundle_map[rel_str] = (part_name, line_cnt, sz_kb, grp)

    # 5. Write 00_PROJECT_MANIFEST.txt
    manifest_file = download_dir / "00_PROJECT_MANIFEST.txt"
    manifest_content = generate_project_manifest(project_root, groups, processed_count)
    with open(manifest_file, "w", encoding="utf-8") as mf:
        mf.write(manifest_content)

    # 6. Write 00_file_structure.txt (Intelligent Minimap & File Locator)
    tree_file = download_dir / "00_file_structure.txt"
    minimap_content = generate_intelligent_minimap(project_root, file_bundle_map, binary_registry_data, tree_lines)
    with open(tree_file, "w", encoding="utf-8") as tf:
        tf.write(minimap_content)

    # 7. Write code bundle parts
    report_rows = [
        (manifest_file.name, manifest_file.stat().st_size),
        (tree_file.name, tree_file.stat().st_size)
    ]

    for i, part_items in enumerate(parts, start=1):
        part_name = f"code_bundle_part{i:02d}of{count:02d}.txt" if count > 1 else "code_bundle.txt"
        toc = "\n".join(
            f"  - {rel_str:<42} ({item_metadata[rel_str][0]:>5,} lines, {item_metadata[rel_str][1]:>6,.1f} KB)"
            for rel_str, _ in part_items
        )
        banner = (
            f"{divider}\n"
            f"CODE EXPORT BUNDLE - PART {i} OF {count}\n"
            f"Project:          {APP_NAME}\n"
            f"Context Manifest: 00_PROJECT_MANIFEST.txt\n"
            f"File Minimap:     00_file_structure.txt\n"
            f"Files in this bundle ({len(part_items)}):\n"
            f"{toc}\n"
            f"{divider}\n\n"
        )
        part_body = "".join(block for _, block in part_items)
        full_payload = banner + part_body
        with open(download_dir / part_name, "w", encoding="utf-8", errors="replace") as out:
            out.write(full_payload)
        report_rows.append((part_name, len(full_payload.encode('utf-8'))))

    # 8. Print comprehensive upload report
    print()
    print("UPLOAD REPORT - Upload all files below as attachments to online AI models:")
    print("-" * 80)
    grand_total = 0
    for name, byte_size in report_rows:
        grand_total += byte_size
        print(f"  {name:<38} {byte_size / 1024.0:>10,.1f} KB   ~{byte_size // 4:>9,} tokens")
    print("-" * 80)
    print(f"  TOTAL: {len(report_rows)} files (<= 10 limit respected), {grand_total / 1024.0:,.1f} KB")
    print()
    print(f"Success! Aggregated {processed_count} files into {len(parts)} bundle(s) + 1 manifest + 1 intelligent minimap.")

def purge_bytecode_cache():
    """Removes any __pycache__ / .pyc this tool's folder has accumulated."""
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
