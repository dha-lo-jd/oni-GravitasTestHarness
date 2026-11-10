"""Check and repair ONI's mods.json before every verification run.

ONI disables a mod (``enabled=false``, ``crash_count`` incremented) when the game dies while
loading it, and leaves ``mod_load_in_progress=true`` behind. After a crash during a verification
cycle the mods under test would silently stay off, so the probe calls ``ensure()`` before each launch.

Usage:
    python mods_json.py check   [--mods-dir DIR] [--id STATIC_ID ...] [--exclusive]
    python mods_json.py fix     [same options]

Exit code of ``check``: 0 = nothing to fix, 1 = problems found. ``fix`` exits 0 on success.
Never run while the game is running (ONI rewrites the file on exit).
"""
import argparse
import json
import os
import shutil
import sys
import time
from pathlib import Path

HARNESS_ID = "Gravitas.TestHarness.Local"
# Last known-good ``enabledForDlc`` per mod (ONI clears it when it disables a mod). Git-ignored.
STATE_FILE = Path(__file__).with_name(".mods_state.json")


def mods_json_path(mods_dir):
    return Path(mods_dir) / "mods.json"


def _load(path):
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), b"\r\n" in raw


def _dump(data, crlf):
    text = json.dumps(data, indent=2, ensure_ascii=False)
    return (text.replace("\n", "\r\n") if crlf else text).encode("utf-8")


def _load_state():
    try:
        return json.loads(STATE_FILE.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return {}


def _save_state(state):
    STATE_FILE.write_text(json.dumps(state, indent=2), encoding="utf-8")


def _remember(data, ids, state):
    """Record enabledForDlc of required mods that are currently healthy. Returns True if state changed."""
    changed = False
    for m in data.get("mods", []):
        sid = m.get("staticID")
        if sid in ids and m.get("enabled") and m.get("enabledForDlc") and state.get(sid) != m["enabledForDlc"]:
            state[sid] = m["enabledForDlc"]
            changed = True
    return changed


def _dlc_fallback(sid, state):
    """enabledForDlc to restore for ``sid``: its own last good value, else any other recorded one."""
    return state.get(sid) or next(iter(state.values()), None)


def inspect(data, required_ids, exclusive=False, state=None):
    """Return (problems, fixes). ``fixes`` are callables that mutate ``data``."""
    state = state or {}
    problems, fixes = [], []
    by_id = {m.get("staticID"): m for m in data.get("mods", [])}

    if data.get("mod_load_in_progress"):
        problems.append("mod_load_in_progress is true (previous run died while loading mods)")
        fixes.append(lambda: data.__setitem__("mod_load_in_progress", False))

    for sid in required_ids:
        mod = by_id.get(sid)
        if mod is None:
            problems.append(f"{sid}: not registered in mods.json (start the game once and register it)")
            continue
        if not mod.get("enabled"):
            problems.append(f"{sid}: disabled")
            fixes.append(lambda m=mod: m.__setitem__("enabled", True))
        if mod.get("crash_count", 0):
            problems.append(f"{sid}: crash_count={mod['crash_count']}")
            fixes.append(lambda m=mod: m.__setitem__("crash_count", 0))
        if mod.get("status") != 1:
            problems.append(f"{sid}: status={mod.get('status')} (not Installed)")
        if not mod.get("enabledForDlc"):
            dlc = _dlc_fallback(sid, state)
            if dlc:
                problems.append(f"{sid}: enabledForDlc is empty")
                fixes.append(lambda m=mod, v=list(dlc): m.__setitem__("enabledForDlc", v))
            else:
                problems.append(f"{sid}: enabledForDlc is empty and no known-good value is recorded "
                                "(enable it once from the in-game mod screen, then run check)")

    if exclusive:
        for sid, mod in by_id.items():
            if sid not in required_ids and mod.get("enabled"):
                problems.append(f"{sid}: enabled but not required (exclusive mode)")
                fixes.append(lambda m=mod: m.__setitem__("enabled", False))
    return problems, fixes


def ensure(mods_dir, required_ids, exclusive=False, fix=True):
    """Check (and optionally repair) mods.json. Returns a dict suitable for JSON output."""
    path = mods_json_path(mods_dir)
    if not path.exists():
        return {"ok": False, "path": str(path), "problems": ["mods.json not found"], "fixed": False}
    data, crlf = _load(path)
    ids = list(dict.fromkeys([HARNESS_ID, *required_ids]))
    state = _load_state()
    if _remember(data, ids, state):
        _save_state(state)
    problems, fixes = inspect(data, ids, exclusive, state)
    fixed = False
    if fix and fixes:
        backup = path.with_name(f"mods.json.probe-backup-{time.strftime('%Y%m%d-%H%M%S')}")
        shutil.copy2(path, backup)
        for f in fixes:
            f()
        path.write_bytes(_dump(data, crlf))
        fixed = True
        _prune_backups(path.parent)
    # Problems that remain after fixing (unregistered mod, empty enabledForDlc, ...) need a human.
    if fixed:
        remaining, _ = inspect(data, ids, exclusive, state)
    else:
        remaining = problems
    return {"ok": not remaining, "path": str(path), "problems": problems,
            "remaining": remaining, "fixed": fixed}


def _prune_backups(directory, keep=5):
    backups = sorted(directory.glob("mods.json.probe-backup-*"))
    for old in backups[:-keep]:
        old.unlink()


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("command", choices=["check", "fix"])
    ap.add_argument("--mods-dir", default=os.environ.get("ONI_MODS_DIR"),
                    help="ONI mods directory containing mods.json (default: $ONI_MODS_DIR)")
    ap.add_argument("--id", action="append", default=[], help="staticID that must be enabled (repeatable)")
    ap.add_argument("--exclusive", action="store_true", help="also disable every other mod")
    args = ap.parse_args(argv)
    if not args.mods_dir:
        ap.error("--mods-dir or ONI_MODS_DIR is required")
    result = ensure(args.mods_dir, args.id, args.exclusive, fix=args.command == "fix")
    print(json.dumps(result, indent=2, ensure_ascii=False))
    if args.command == "check":
        return 0 if not result["problems"] else 1
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
