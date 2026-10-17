"""
═══════════════════════════════════════════════════════════════════════════════════════════════
REDUNDANT CODE FINDER — WRITES TWO REPORTS. READ THE RIGHT ONE.

   ┌───────────────────────────────────────────────────────────────────────────────────────┐
   │  AI AGENT?  READ  developer_tools/redundant_code_report.jsonl   — AND NOTHING ELSE.    │
   │  HUMAN?     OPEN  developer_tools/redundant_code_report.html    in a browser.          │
   └───────────────────────────────────────────────────────────────────────────────────────┘

⚠️ AGENTS: DO NOT OPEN THE .html. It is ~5.5x larger and every extra byte is styling, colour and
a SECOND FULL COPY of code you can already read from disk. It exists so a person can review
findings visually. Reading it wastes context and tells you nothing the .jsonl does not.

THE .jsonl IN ONE PARAGRAPH: line 1 is a header holding the run's settings and a `files` array;
every later line is one finding. Parse with `for line in f: json.loads(line)`. A finding gives
a similarity score and two coordinates — file index, first line, last line. Open those ranges
yourself; the code is on disk and is deliberately NOT duplicated into the report.

Both reports are deleted and rewritten on every run, so whatever is on disk is always current.
═══════════════════════════════════════════════════════════════════════════════════════════════
"""

import sys
import os

# ══════════════════════════════════════════════════════════════════════════════════════════
# NO CACHE. THESE TWO LINES COME BEFORE EVERY OTHER IMPORT, AND THAT ORDER IS THE POINT.
# `dont_write_bytecode` only governs imports that happen AFTER it is set, so setting it below
# the import block (where it used to be) protects nothing that was already imported. The env
# var covers any child process this script might ever spawn.
# This does NOT protect against someone IMPORTING this file from another script — Python then
# obeys the IMPORTER's setting, not ours, and drops a __pycache__ next to this file. That case
# is swept up by purge_bytecode_cache() at the end of the run.
# ══════════════════════════════════════════════════════════════════════════════════════════
sys.dont_write_bytecode = True
os.environ['PYTHONDONTWRITEBYTECODE'] = '1'

import re
import html
import json
import shutil
import datetime
from collections import defaultdict
from pathlib import Path

WORKING_DIRECTORY = Path(__file__).resolve().parent.parent

try:
    os.chdir(WORKING_DIRECTORY)
except Exception as e:
    print(f"Failed to change working directory: {e}")
    sys.exit(1)

EXCLUDE_FOLDERS = ['.git', 'bin', 'obj', '.vs', 'packages', 'compile', 'compiled', 'old_code', 'build', 'developer_tools', '.devlogs']
EXCLUDE_FILES = ['AssemblyInfo.cs']

# (8) EXCLUDE_EXTS was removed. It listed .txt/.json/.dll/... but could never do anything,
# because the file walk below keeps ONLY the extensions in TARGET_EXTS anyway. Saying what we
# DO scan is honest; a long deny-list that never runs implies the tool reads more than it does.
TARGET_EXTS = {'.cs'}

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

# ══════════════════════════════════════════════════════════════════════════════════════════
# (4) WHAT COUNTS AS "LOGIC" — AND WHY THE OLD TEST LET DATA THROUGH.
#
# The new rule, checked against exact TOKENS rather than substrings:
#   a block is logic if it contains a control-flow WORD, or a real multi-character OPERATOR.
# A lone `=`, and bare `<` / `>`, deliberately do NOT qualify.
# ══════════════════════════════════════════════════════════════════════════════════════════
LOGIC_WORDS = frozenset([
    "if", "else", "for", "foreach", "while", "do", "switch", "case",
    "return", "try", "catch", "finally", "throw", "goto", "lock", "yield",
])

LOGIC_OPERATORS = ("==", "!=", "<=", ">=", "&&", "||", "+=", "-=", "*=", "/=", "%=", "??", "++", "--")

# (5) A later match is hidden only when THIS much of BOTH its sides was already reported.
OVERLAP_SUPPRESS_RATIO = 0.60

# ══════════════════════════════════════════════════════════════════════════════════════════
# FUZZY MATCHING — "these two blocks are ~85% the same", not just "identical".
# ══════════════════════════════════════════════════════════════════════════════════════════
MIN_SIMILARITY = 0.80          # a block is kept while at least this share of its lines match
MAX_CONSECUTIVE_GAP = 3        # ...and while no more than this many differ back to back
MIN_BLOCK_LINES = 6            # nothing shorter than this is worth a human's attention

# ══════════════════════════════════════════════════════════════════════════════════════════
# A BLOCK MUST SAY SOMETHING.
# ══════════════════════════════════════════════════════════════════════════════════════════
MIN_SUBSTANTIVE_LINES = 2
_STRUCTURAL_ONLY = frozenset(["{", "}", "});", "};", ")", ");", "else", "try", "catch", "finally", "break;", "continue;"])
_SUBSTANTIVE_RE = re.compile(r'[A-Za-z_]\w*\s*[\(=\.]')


def count_substantive(code_text):
    """How many lines in this block actually do something, as opposed to bracing something."""
    n = 0
    for raw_line in code_text.split("\n"):
        line = raw_line.strip()
        if len(line) < 12 or line in _STRUCTURAL_ONLY:
            continue
        if _SUBSTANTIVE_RE.search(line):
            n += 1
    return n

def get_target_files(root_dir):
    targets = []
    for root, dirs, files in os.walk(root_dir):
        dirs[:] = [d for d in dirs if d.lower() not in EXCLUDE_FOLDERS]
        for file in files:
            if file in EXCLUDE_FILES: continue
            _, ext = os.path.splitext(file)
            if ext.lower() in TARGET_EXTS:
                targets.append(os.path.join(root, file))
    return targets

def display_path(filepath):
    try:
        return str(Path(filepath).resolve().relative_to(WORKING_DIRECTORY))
    except Exception:
        return str(filepath)

def remove_comments(source):
    """
    Strips C# comments and blanks out string/char literals IN ONE LEFT-TO-RIGHT PASS.
    """
    out = []
    i, n = 0, len(source)
    while i < n:
        c = source[i]

        # @"verbatim" — no backslash escapes; a doubled "" is a literal quote
        if c == '@' and i + 1 < n and source[i + 1] == '"':
            out.append('""')
            i += 2
            while i < n:
                if source[i] == '"':
                    if i + 1 < n and source[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                if source[i] == '\n':
                    out.append('\n')
                i += 1
            continue

        if c == '"':
            out.append('""')
            i += 1
            while i < n:
                if source[i] == '\\':
                    i += 2
                    continue
                if source[i] == '"':
                    i += 1
                    break
                if source[i] == '\n':      # unterminated: do not swallow the line break
                    out.append('\n')
                    i += 1
                    break
                i += 1
            continue

        if c == "'":
            out.append("''")
            i += 1
            while i < n:
                if source[i] == '\\':
                    i += 2
                    continue
                if source[i] == "'":
                    i += 1
                    break
                if source[i] == '\n':
                    out.append('\n')
                    i += 1
                    break
                i += 1
            continue

        if c == '/' and i + 1 < n and source[i + 1] == '/':
            while i < n and source[i] != '\n':
                i += 1
            continue

        if c == '/' and i + 1 < n and source[i + 1] == '*':
            i += 2
            while i + 1 < n and not (source[i] == '*' and source[i + 1] == '/'):
                if source[i] == '\n':
                    out.append('\n')       # (3) the fix: keep the line count intact
                i += 1
            i = min(i + 2, n)
            continue

        out.append(c)
        i += 1

    return "".join(out)

def normalize_line(line):
    """Returns (fingerprint, tokens). Literals are already blanked by remove_comments."""
    words = re.findall(r'[a-zA-Z_]\w*|\d+|[^a-zA-Z_\d\s]', line)
    norm = []
    for w in words:
        if w.isdigit():
            norm.append("0")
        elif re.match(r'^[a-zA-Z_]\w*$', w):
            norm.append(w if w in KEYWORDS else "V")
        else:
            norm.append(w)
    return "".join(norm), norm

def block_has_logic(token_lines):
    """(4) `token_lines` is a sequence of TOKEN LISTS — one per source line."""
    joined = []
    for toks in token_lines:
        for t in toks:
            if t in LOGIC_WORDS:
                return True          # a control-flow word settles it outright
        joined.extend(toks)

    text = "".join(joined)
    return any(op in text for op in LOGIC_OPERATORS)

def _same_shape(lines_db, a, b):
    return lines_db[a][3] == lines_db[b][3]


def grow_fuzzy_block(lines_db, idx1, idx2, seed_len):
    """
    Grows the seed at (idx1, idx2) outwards while it stays >= MIN_SIMILARITY.

    Returns (start1, start2, length, matched_lines) or None when the result is not worth
    reporting. `idx1 < idx2` is guaranteed by the caller.
    """
    file1, file2 = lines_db[idx1][0], lines_db[idx2][0]
    same_file = file1 == file2
    n = len(lines_db)

    def in_bounds(a, b):
        return 0 <= a < n and 0 <= b < n and lines_db[a][0] == file1 and lines_db[b][0] == file2

    # ---- forward from the end of the seed -------------------------------------------------
    matched = total = seed_len
    best_fwd, best_fwd_matched = seed_len, seed_len
    misses = 0
    off = seed_len
    while True:
        a, b = idx1 + off, idx2 + off
        if not in_bounds(a, b):
            break
        # Two regions of ONE file must never be allowed to overlap each other.
        if same_file and a >= idx2:
            break
        total += 1
        if _same_shape(lines_db, a, b):
            matched += 1
            misses = 0
        else:
            misses += 1
            if misses > MAX_CONSECUTIVE_GAP:
                break
        if matched / total >= MIN_SIMILARITY:
            best_fwd, best_fwd_matched = off + 1, matched
        off += 1

    # ---- backward from the start of the seed ----------------------------------------------
    matched, total = best_fwd_matched, best_fwd
    best_back, best_back_matched = 0, best_fwd_matched
    misses = 0
    step = 1
    while True:
        a, b = idx1 - step, idx2 - step
        if not in_bounds(a, b):
            break
        # Growing the LEFT block leftwards can only widen the gap to the right block, but the
        # right block's own head must not reach back into the left block.
        if same_file and b <= idx1:
            break
        total += 1
        if _same_shape(lines_db, a, b):
            matched += 1
            misses = 0
        else:
            misses += 1
            if misses > MAX_CONSECUTIVE_GAP:
                break
        if matched / total >= MIN_SIMILARITY:
            best_back, best_back_matched = step, matched
        step += 1

    length = best_back + best_fwd
    start1, start2 = idx1 - best_back, idx2 - best_back

    # ⚠️ TRIM THE ENDS BACK TO MATCHING LINES.
    while length > 0 and not _same_shape(lines_db, start1 + length - 1, start2 + length - 1):
        length -= 1
    while length > 0 and not _same_shape(lines_db, start1, start2):
        start1 += 1
        start2 += 1
        length -= 1

    if length < MIN_BLOCK_LINES:
        return None

    matched = sum(1 for k in range(length) if _same_shape(lines_db, start1 + k, start2 + k))
    if matched / length < MIN_SIMILARITY:
        return None

    if same_file and start1 + length > start2:      # final belt-and-braces overlap check
        return None
    return start1, start2, length, matched


def generate_html_report(matches, output_path):
    html_parts = []
    html_parts.append("""<!-- ==========================================================================
     AI AGENT: STOP. THIS IS THE HUMAN REPORT. DO NOT READ FURTHER.
     Read developer_tools/redundant_code_report.jsonl instead - same findings, ~5.5x smaller,
     one JSON object per line, no styling and no duplicated code bodies.
     This file exists only so a person can review the findings visually in a browser.
     ========================================================================== -->
<!DOCTYPE html>
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
        .badge-row { text-align:center; margin:0 0 10px 0; }
        .badge { display:inline-block; border-radius:4px; padding:3px 14px;
                 font-size:0.8em; font-weight:bold; letter-spacing:0.5px; }
        .badge-same  { background:#4a3a12; color:#ffd479; border:1px solid #7a6320; }
        .badge-cross { background:#123a4a; color:#79d4ff; border:1px solid #206a7a; }
        .badge-exact { background:#173a1e; color:#8fe39a; border:1px solid #2f6b3a; margin-left:8px; }
        .badge-near  { background:#3a2417; color:#f0a878; border:1px solid #7a4a20; margin-left:8px; }
        .summary { text-align:center; color:#9aa0a6; font-size:0.9em; margin-bottom:24px; }
        .agent-route { background:#3a1720; color:#ffb4c0; border:1px solid #7a2033;
                       border-radius:6px; padding:10px 16px; margin-bottom:18px;
                       font-size:0.9em; text-align:center; }
        .agent-route code { color:#ffd479; }
    </style>
</head>
<body>
    <div class="agent-route">
        <b>AI AGENT?</b> You are in the wrong file. Read
        <code>developer_tools/redundant_code_report.jsonl</code> instead &mdash; identical
        findings, about 5.5&times; smaller, one JSON object per line, and it does not repeat code
        you can already read from disk. <b>This page is the human route.</b>
    </div>
    <h1>Redundant Code Finder - Analysis Report</h1>
    <p style="text-align:center;">Fuzzy-matched duplicates (ignoring spaces, comments, variable names, strings, and numbers).</p>
    <p class="summary">
        <span class="badge badge-same">DUPLICATES WITHIN THE SAME FILE ITSELF</span>
        &nbsp;=&nbsp; both halves live in ONE file, at two different places.<br><br>
        <span class="badge badge-cross">DUPLICATES FROM TWO DIFFERENT FILES</span>
        &nbsp;=&nbsp; the same block appears in two separate files.<br><br>
        <span class="badge badge-exact">IDENTICAL</span>
        &nbsp;=&nbsp; every line matches.
        &nbsp;&nbsp;<span class="badge badge-near">NEAR 87%</span>
        &nbsp;=&nbsp; that share of lines match; the rest differ.
        Similarity is a LOWER bound &mdash; the two sides are compared line-for-line, so a block
        with an extra line inserted on one side scores worse than it really is.
    </p>
    <p class="summary">__COUNTS__</p>
""")

    n_same = sum(1 for m in matches if m['file1'] == m['file2'])
    n_cross = len(matches) - n_same
    html_parts[-1] = html_parts[-1].replace(
        "__COUNTS__",
        f"<b>{n_same}</b> within one file &nbsp;&bull;&nbsp; <b>{n_cross}</b> across two files "
        f"&nbsp;&bull;&nbsp; <b>{len(matches)}</b> total")

    if not matches:
        html_parts.append("<p style='text-align:center; color:#c586c0;'>No significant redundancies found!</p>")

    for idx, match in enumerate(matches):
        e = html.escape
        same_file = match['file1'] == match['file2']

        span1 = match['line1_end'] - match['line1_start'] + 1
        span2 = match['line2_end'] - match['line2_start'] + 1
        span_note = (f"{match['len']} code lines"
                     f" &mdash; spanning {span1} lines here"
                     f"{'' if span1 == span2 else f' and {span2} there'}")

        badge_class = "badge-same" if same_file else "badge-cross"
        badge_text = ("DUPLICATES WITHIN THE SAME FILE ITSELF" if same_file
                      else "DUPLICATES FROM TWO DIFFERENT FILES")

        sim = match.get('similarity', 1.0)
        exact = sim >= 0.9999
        sim_class = "badge-exact" if exact else "badge-near"
        sim_text = ("IDENTICAL" if exact
                    else f"NEAR {sim * 100:.0f}% &mdash; {match['len'] - match['matched']} line(s) differ")

        html_parts.append(f"""
    <div class="match-card">
        <div class="match-header">
            <strong>Match #{idx+1}</strong> - <span class="stat">{span_note}</span>
        </div>
        <div class="badge-row"><span class="badge {badge_class}">{badge_text}</span><span class="badge {sim_class}">{sim_text}</span></div>
        <div class="flex-container">
            <div class="code-col">
                <div class="file-title">{e(match['file1'])} (Lines {match['line1_start']} - {match['line1_end']})</div>
                <pre>{e(match['code1'])}</pre>
            </div>
            <div class="code-col">
                <div class="file-title">{'&#8627; same file' if same_file else e(match['file2'])} (Lines {match['line2_start']} - {match['line2_end']})</div>
                <pre>{e(match['code2'])}</pre>
            </div>
        </div>
    </div>
""")

    html_parts.append("</body></html>")
    with open(output_path, 'w', encoding='utf-8') as f:
        f.write("".join(html_parts))
    print(f"\n[+] Report saved to: {output_path}")

def generate_agent_report(matches, output_path, stats):
    """
    OUTPUT 2 — for a machine. Maximum signal per byte; no concession to human readability.
    """
    file_ids, file_list = {}, []

    def fid(path):
        norm = path.replace("\\", "/")
        if norm not in file_ids:
            file_ids[norm] = len(file_list)
            file_list.append(norm)
        return file_ids[norm]

    rows = []
    for i, m in enumerate(matches, 1):
        sim = m.get('similarity', 1.0)
        rows.append({
            "i": i,
            "k": 0 if m['file1'] == m['file2'] else 1,
            "sim": round(sim, 3),
            "n": m['len'],
            "d": m['len'] - m.get('matched', m['len']),
            "a": [fid(m['file1']), m['line1_start'], m['line1_end']],
            "b": [fid(m['file2']), m['line2_start'], m['line2_end']],
            "h": m['code1'].split("\n")[0][:100],
        })

    meta = {
        "schema": "redundant-code/2",
        "READ_THIS_NOT_THE_HTML": (
            "You are in the correct file. The sibling redundant_code_report.html is ~5.5x larger, "
            "is styled for human eyes and repeats every code block in full. Do not open it."
        ),
        "how_to_use": (
            "Line 1 is this header. Every later line is one finding: json.loads it. "
            "a and b are [fileIndex, firstLine, lastLine] into files[]. Read those ranges from "
            "disk yourself - code bodies are intentionally not included here. "
            "Findings are sorted longest first; start at the top."
        ),
        "record": {
            "i": "id", "k": "0=two places in ONE file, 1=two DIFFERENT files",
            "sim": "0.80-1.00 share of lines identical; LOWER BOUND, see caveats",
            "n": "block length in code lines", "d": "how many of n differ",
            "a": "[fileIndex, firstLine, lastLine]", "b": "same, the other side",
            "h": "first code line, for recognition only",
        },
        "caveats": [
            "Comparison is on NORMALISED lines: identifiers -> V, numbers -> 0, literals -> empty, "
            "whitespace dropped. Two blocks differing only in names/values score 1.0 - that is intended.",
            "Sides advance in lockstep, so only substitutions are modelled, not insertions. A block "
            "with an extra line on one side scores lower than it deserves. sim never overstates.",
            "A pair sharing no seed_lines identical lines in a row is not found at all.",
            "sim 1.0 means identical AFTER normalisation, not byte-identical.",
        ],
        "settings": {
            "seed_lines": stats["seed"], "min_similarity": MIN_SIMILARITY,
            "max_consecutive_gap": MAX_CONSECUTIVE_GAP, "min_block_lines": MIN_BLOCK_LINES,
            "overlap_suppress_ratio": OVERLAP_SUPPRESS_RATIO,
        },
        "totals": {
            "files_scanned": stats["files"], "code_lines_scanned": stats["lines"],
            "raw_matches": stats["raw"], "suppressed_overlapping": stats["suppressed"],
            "discarded_hollow": stats["hollow"],
            "reported": len(rows),
            "same_file": sum(1 for r in rows if r["k"] == 0),
            "cross_file": sum(1 for r in rows if r["k"] == 1),
            "identical": sum(1 for r in rows if r["sim"] >= 0.9999),
            "near": sum(1 for r in rows if r["sim"] < 0.9999),
        },
        "generated_utc": datetime.datetime.now(datetime.timezone.utc)
                         .replace(microsecond=0).isoformat(),
        "root": str(WORKING_DIRECTORY).replace("\\", "/"),
        "files": file_list,
    }

    with open(output_path, 'w', encoding='utf-8') as f:
        f.write(json.dumps(meta, separators=(',', ':')) + "\n")
        for r in rows:
            f.write(json.dumps(r, separators=(',', ':')) + "\n")

    print(f"[+] Agent report saved to: {output_path}")


def purge_bytecode_cache():
    """
    Removes any __pycache__ / .pyc this tool's own folder has accumulated.
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


def main():
    print("--- REDUNDANT CODE FINDER ---")
    purge_bytecode_cache()

    dev_tools = Path(WORKING_DIRECTORY) / "developer_tools"
    for stale in ("redundant_code_report.html", "redundant_code_report.jsonl"):
        target = dev_tools / stale
        if not target.exists():
            continue
        try:
            target.unlink()
            print(f"[*] Removed previous {stale}")
        except Exception as exc:
            try:
                with open(target, 'w', encoding='utf-8'):
                    pass
                print(f"[*] Could not delete {stale} ({exc.__class__.__name__}); emptied it instead.")
            except Exception as exc2:
                print(f"[!] Could not delete OR empty {stale}: {exc2}")
                print(f"[!] If this run fails, that file will be STALE. Do not trust it.")

    files = get_target_files(WORKING_DIRECTORY)
    print(f"[*] Scanning {len(files)} files...")

    lines_db = []
    
    # 1. Parse all files and normalize lines
    for filepath in files:
        rel_path = display_path(filepath)
        try:
            with open(filepath, 'r', encoding='utf-8-sig') as f:
                original = f.read()
            cleaned = remove_comments(original)

            original_lines = original.split('\n')

            for line_num, line_text in enumerate(cleaned.split('\n'), 1):
                stripped = line_text.strip()
                if not stripped: continue

                norm, toks = normalize_line(stripped)
                if not norm: continue

                display = original_lines[line_num - 1].strip() if line_num <= len(original_lines) else stripped
                lines_db.append((rel_path, line_num, display, norm, toks))
        except Exception as e:
            print(f"Error reading {rel_path}: {e}")

    # 2. Build 6-gram hash map
    print("[*] Hashing code logic blocks...")
    N = 6
    ngram_map = defaultdict(list)
    
    for i in range(len(lines_db) - N + 1):
        if lines_db[i][0] == lines_db[i + N - 1][0]:
            ngram = tuple(lines_db[i + j][3] for j in range(N))
            if block_has_logic([lines_db[i + j][4] for j in range(N)]):
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
                    
                grown = grow_fuzzy_block(lines_db, idx1, idx2, N)
                if grown is None:
                    continue
                start1, start2, match_len, matched_lines = grown

                if block_has_logic([lines_db[start1 + k][4] for k in range(match_len)]):
                    for k in range(match_len):
                        visited_pairs.add((start1 + k, start2 + k))
                        visited_pairs.add((start2 + k, start1 + k))

                    block1 = lines_db[start1:start1 + match_len]
                    block2 = lines_db[start2:start2 + match_len]
                    similarity = matched_lines / match_len

                    matches.append({
                        'len': match_len,
                        'matched': matched_lines,
                        'similarity': similarity,
                        'file1': block1[0][0],
                        'line1_start': block1[0][1],
                        'line1_end': block1[-1][1],
                        'file2': block2[0][0],
                        'line2_start': block2[0][1],
                        'line2_end': block2[-1][1],
                        'code1': "\n".join(x[2] for x in block1),
                        'code2': "\n".join(x[2] for x in block2)
                    })

    # 4. Sort, de-overlap, output
    print(f'Sorting {len(matches)} matches...')
    matches.sort(key=lambda x: x['len'], reverse=True)

    covered = defaultdict(set)

    def fraction_already_reported(path, start, end):
        span = range(min(start, end), max(start, end) + 1)
        if not span:
            return 1.0
        seen = covered[path]
        return sum(1 for ln in span if ln in seen) / len(span)

    kept, suppressed, hollow = [], 0, 0
    for mt in matches:
        if count_substantive(mt['code1']) < MIN_SUBSTANTIVE_LINES:
            hollow += 1
            continue
        f1 = fraction_already_reported(mt['file1'], mt['line1_start'], mt['line1_end'])
        f2 = fraction_already_reported(mt['file2'], mt['line2_start'], mt['line2_end'])
        if f1 >= OVERLAP_SUPPRESS_RATIO and f2 >= OVERLAP_SUPPRESS_RATIO:
            suppressed += 1
            continue
        kept.append(mt)
        covered[mt['file1']].update(range(mt['line1_start'], mt['line1_end'] + 1))
        covered[mt['file2']].update(range(mt['line2_start'], mt['line2_end'] + 1))

    print(f"[*] Discarded {hollow} hollow blocks (braces/keywords with no real statement).")
    print(f"[*] Suppressed {suppressed} near-duplicate reports of regions already listed.")

    # OUTPUT 1 — for a human: styled, complete, both code bodies shown side by side.
    generate_html_report(kept, str(dev_tools / "redundant_code_report.html"))

    # OUTPUT 2 — for an agent: same findings, coordinates only, one JSON object per line.
    generate_agent_report(kept, str(dev_tools / "redundant_code_report.jsonl"), {
        "seed": N,
        "files": len(files),
        "lines": len(lines_db),
        "raw": len(matches),
        "suppressed": suppressed,
        "hollow": hollow,
    })

    purge_bytecode_cache()   # nothing this tool leaves behind should outlive the run

    n_same = sum(1 for m in kept if m['file1'] == m['file2'])
    n_near = sum(1 for m in kept if m.get('similarity', 1.0) < 0.9999)
    print(f"[*] Analysis Complete. {len(kept)} blocks reported from {len(matches)} raw matches "
          f"({n_same} within one file, {len(kept) - n_same} across files; "
          f"{len(kept) - n_near} identical, {n_near} near-duplicates).")

if __name__ == "__main__":
    main()
