# Developer Workflow, CI/CD & Release

> Read `project_structure.txt` (mandatory rules + hardware guard rails) before this file. Route: [`docs/index.md`](../index.md).

# 3. DEVELOPER WORKFLOW, CI/CD & DEPLOYMENT (`dev.cmd`, `dev_build.cmd`, `build.cmd`, `build\*`, `developer_tools\*`)
- **Interactive Dev Workflow (`.\dev.cmd` & `build\DevSandbox.ps1`)**:
  - Supports `dev` (incremental `dotnet watch` hot-reload with `-p:PublishAot=false -- run-ui`), `dev rebuild` (full `bin`/`obj` wipe + watch), `dev run` (single clean Debug launch), `dev build` (Debug compile only), `dev fresh` (wipes `%LOCALAPPDATA%\KeyPulse_DEV` for clean-slate first-boot testing), `dev trace` (routes logs to `.devlogs\` inside the repo), `dev restore`, and `dev clean`.
  - `build\DevSandbox.ps1` manages `%LOCALAPPDATA%\KeyPulse_DEV\data` and `%LOCALAPPDATA%\KeyPulse_DEV\keep`, seeded once from a clean run and never copied from `%APPDATA%\KeyPulse`.
- **Pre-Build Fix Sentinel Verifier (`build\sentinels.txt` & `build\VerifySentinels.ps1` / `SYS-VERIFYTOOL`)**:
  - Automatically runs before every `dev.cmd`, `dev_build.cmd`, and `build.cmd` invocation, verifying that 20 critical regression guards across the codebase exist in their target source files before compilation begins.
- **Repo-Scoped Process Termination (`BUILDKILL_01`)**:
  - Both `dev.cmd` and `build\BuildPipeline.ps1` (`Stop-RepoProcesses`) inspect process `ExecutablePath` / `MainModule.FileName` and terminate **only** processes whose executable resides inside this repository (`c:\Software_Development\KeyPulse`), leaving the user's installed `%LOCALAPPDATA%\Programs\KeyPulse\KeyPulse.exe` running undisturbed.
- **Local NativeAOT Test Build (`.\dev_build.cmd`)**:
  - Compiles the full standalone NativeAOT `.\compiled\KeyPulse.exe` with `0.0.0-dev.<timestamp>` metadata while leaving `version.txt`, `Directory.Build.props`, `KeyPulse.csproj`, `app.manifest`, Git commits/tags, and GitHub completely untouched.
- **Live Colored Build Logger (`developer_tools\BuildLog.ps1`)**:
  - Streams live colored console output, enforces `-p:TreatWarningsAsErrors=true`, and writes `developer_tools\build.log` (ANSI-colored) and `developer_tools\build.result.txt` with an instant first-line verdict (`OK / WARN / FAIL | X errors | Y warnings | exit code | duration`) plus top and bottom summary banners, keeping the repository root directory clean.
- **Official Release Pipeline (`.\build.cmd` & `build\BuildPipeline.ps1`)**:
  - **Quality Gate (Step 0.5)**: Runs automated test suite (`dotnet test tests\KeyPulse.Tests`) before preflight or compilation. Any test failure aborts the pipeline.
  - **Pre-Release Gate (`RELGATE_01`)**: Supports `--beta` and `--no-publish`. Executes non-interactively without blocking confirmation prompts when invoking `.\build.cmd`. Verifies default branch (`main`/`master`), fast-forward remote state (`origin/<branch>` is an ancestor of `HEAD`), unused tag `vYYYY.MM.DD.HHmm` (`-beta` when `--beta`), and `gh auth status`.
  - **Autonomous Changelog Promotion & Version Synchronization**: Synchronizes `version.txt`, `Directory.Build.props`, `KeyPulse.csproj`, and `app.manifest` prior to compilation. Automatically promotes `## [Unreleased]` notes in `docs/CHANGELOG.md` to `## [$Tag] - YYYY-MM-DD` and extracts them for the GitHub Release notes. Requires a clean working tree and stages only `$releaseFiles` (version files + `docs/CHANGELOG.md`), while `.gitignore` strictly prevents 46 MB executables and temporary build files from polluting git history. If a post-build step fails, `Undo-ReleasePlan` rolls back the local commit via `git reset --mixed` (preserving all working tree changes) and deletes the local tag.
  - **NativeAOT `ILCompiler` Crash Auto-Retry (`ILCCRASH_01`)**: `Invoke-NativeAotPublish` detects `.NET 9` parallel `ILCompiler` scanner crashes (`NativeAotFatalErrorException`, `CodeGenerationFailedException`, `error IL1013`) and automatically retries publish once with `-p:IlcSingleThreaded=true`.
  - **Draft-First Upload, SHA-256 Verification & Checksum Manifests (`RELTAG_01` / `SIG_01`)**: Generates detached `SHA256SUMS.txt`, signs via Authenticode hook if certificate is present, uploads `.\compiled\KeyPulse.exe` + `SHA256SUMS.txt` as a **draft** (`gh release create ... --draft`), verifies the remote asset SHA-256, then `git push --atomic` commit+tag, then promotes (`--draft=false --latest` / `--prerelease` for `--beta`). If remote asset verification or promotion fails, the pipeline automatically purges the draft release and deletes the remote tag (`Undo-ReleasePlan -CleanRemote`) to prevent broken or orphaned release states.
  - **Binary-Only Retention Pruning (`RETAIN_01`)**: Prunes older releases beyond the retention window (**5 stable** and **3 beta** releases kept) by deleting only binary assets (`gh release delete-asset $oldTag KeyPulse.exe`) while preserving all GitHub Release entities, release notes, history, and Git tags.
- **Developer Utilities (`developer_tools\`)**:
  - `developer_tools\scratch\`: Isolated sandbox directory for transient preview icons, design mockups, and test artifacts, eliminating root directory clutter.
  - `developer_tools\Pull_Code_files.py`: AI codebase packager and staging aggregator. Condenses the entire repository into <= 10 files (1 architectural manifest with invariants and sentinels, 1 intelligent minimap and file locator with bundle cross-reference, and capped code bundles with SHA-256 binary placeholders) into `%USERPROFILE%\Downloads\keypulse` for upload to cloud AI models.
  - `developer_tools\Redundant_Code_Finder.py`: AST/token-normalized fuzzy duplicate detector generating `developer_tools/redundant_code_report.html` (for human visual review) and `developer_tools/redundant_code_report.jsonl` (compact coordinate-only report for AI agents).
  - `developer_tools\Manual_UnInstall.bat`: Standalone system wiper removing running `KeyPulse.exe` instances, `KeyPulse_AdminTask`, HKCU `Uninstall`/`Run` registry keys, shortcuts, `%LOCALAPPDATA%\Programs\KeyPulse`, `%APPDATA%\KeyPulse`, and `%LOCALAPPDATA%\KeyPulse_DEV`.

## Directives

- **NEVER kill all `KeyPulse.exe` processes indiscriminately in `dev.cmd` or `BuildPipeline.ps1` (`BUILDKILL_01`).** Only terminate processes whose executable path resides inside the repository directory so the user's installed KeyPulse instance is never killed during development or builds.
- **NEVER remove or weaken a sentinel in `build/sentinels.txt` without explicit justification.** Every fix sentinel is verified by `build\VerifySentinels.ps1` before compilation.
- **NEVER open `developer_tools/redundant_code_report.html` as an AI agent.** Always read `developer_tools/redundant_code_report.jsonl` instead.
