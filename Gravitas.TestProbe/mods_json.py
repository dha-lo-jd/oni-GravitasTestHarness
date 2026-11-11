"""Check and repair ONI's mods.json before every verification run.

Which mods the game loads is decided by ``enabledForDlc`` (it must contain the highest active DLC id,
e.g. ``EXPANSION1_ID``; see ``KMod.Mod.IsEnabledForActiveDlc``). The ``enabled`` flag is legacy: ONI
rewrites it on exit and the game ignores it. A mod stays loaded on every later start until its
``enabledForDlc`` is emptied, so the probe must (a) judge by ``enabledForDlc`` and (b) put the user's
original file back after a run (``snapshot()`` / ``restore()``).

ONI also empties ``enabledForDlc`` and bumps ``crash_count`` when the game dies while loading a mod,
and leaves ``mod_load_in_progress=true`` behind; ``ensure()`` repairs both.

Usage:
    python mods_json.py check   [--mods-dir DIR] [--id STATIC_ID ...] [--exclusive] [--dlc-id ID]
    python mods_json.py fix     [same options]   (persists the change: this is how you set the permanent mod set)

Exit code of ``check``: 0 = nothing to fix, 1 = problems found. ``fix`` exits 0 on success.
Never run while the game is running (ONI rewrites the file on exit).
"""
import argparse
import json
import os
import shutil
import sys
import time
from collections import Counter
from pathlib import Path

HARNESS_ID = "Gravitas.TestHarness.Local"
DEFAULT_DLC_ID = "EXPANSION1_ID"


def mods_json_path(mods_dir):
    return Path(mods_dir) / "mods.json"


def _load(path):
    raw = path.read_bytes()
    return json.loads(raw.decode("utf-8-sig")), b"\r\n" in raw


def _dump(data, crlf):
    text = json.dumps(data, indent=2, ensure_ascii=False)
    return (text.replace("\n", "\r\n") if crlf else text).encode("utf-8")


def active_dlc(data, override=None):
    """The DLC id the game checks: given, else the one most mods use in enabledForDlc, else Spaced Out's."""
    if override:
        return override
    counts = Counter(d for m in data.get("mods", []) for d in (m.get("enabledForDlc") or []))
    return counts.most_common(1)[0][0] if counts else DEFAULT_DLC_ID


def is_on(mod, dlc):
    return dlc in (mod.get("enabledForDlc") or [])


def _switch(mod, dlc, on):
    dlcs = [d for d in (mod.get("enabledForDlc") or []) if d != dlc]
    mod["enabledForDlc"] = dlcs + [dlc] if on else dlcs
    mod["enabled"] = on


def inspect(data, required_ids, exclusive=False, dlc=None):
    """Return (problems, fixes). ``fixes`` are callables that mutate ``data``."""
    dlc = active_dlc(data, dlc)
    problems, fixes = [], []
    # A staticID is not unique (e.g. a Workshop copy and a dev copy of one mod): treat them as a group.
    by_id = {}
    for m in data.get("mods", []):
        by_id.setdefault(m.get("staticID"), []).append(m)

    if data.get("mod_load_in_progress"):
        problems.append("mod_load_in_progress is true (previous run died while loading mods)")
        fixes.append(lambda: data.__setitem__("mod_load_in_progress", False))

    for sid in required_ids:
        group = by_id.get(sid)
        if not group:
            problems.append(f"{sid}: not registered in mods.json (start the game once and register it)")
            continue
        # Already loaded by any copy is enough; otherwise switch on the first one only.
        mod = next((m for m in group if is_on(m, dlc)), group[0])
        if not is_on(mod, dlc):
            problems.append(f"{sid}: not enabled for {dlc}")
            fixes.append(lambda m=mod: _switch(m, dlc, True))
        elif not mod.get("enabled"):
            fixes.append(lambda m=mod: m.__setitem__("enabled", True))  # legacy flag only, not a problem
        if mod.get("crash_count", 0):
            problems.append(f"{sid}: crash_count={mod['crash_count']}")
            fixes.append(lambda m=mod: m.__setitem__("crash_count", 0))
        if mod.get("status") != 1:
            problems.append(f"{sid}: status={mod.get('status')} (not Installed)")

    if exclusive:
        for sid, group in by_id.items():
            if sid in required_ids:
                continue
            for mod in group:
                if is_on(mod, dlc):
                    problems.append(f"{sid} ({mod['label'].get('id')}): enabled but not required (exclusive mode)")
                    fixes.append(lambda m=mod: _switch(m, dlc, False))
    return problems, fixes


def snapshot(mods_dir):
    """Exact bytes of mods.json, to be given to ``restore`` once the game has exited."""
    path = mods_json_path(mods_dir)
    return path.read_bytes() if path.exists() else None


def restore(mods_dir, raw):
    """Put the file captured by ``snapshot`` back. Returns True if the content changed."""
    if raw is None:
        return False
    path = mods_json_path(mods_dir)
    if path.exists() and path.read_bytes() == raw:
        return False
    path.write_bytes(raw)
    return True


def ensure(mods_dir, required_ids, exclusive=False, fix=True, dlc_id=None):
    """Check (and optionally repair) mods.json. Returns a dict suitable for JSON output."""
    path = mods_json_path(mods_dir)
    if not path.exists():
        return {"ok": False, "path": str(path), "problems": ["mods.json not found"], "fixed": False}
    data, crlf = _load(path)
    ids = list(dict.fromkeys([HARNESS_ID, *required_ids]))
    dlc = active_dlc(data, dlc_id)
    problems, fixes = inspect(data, ids, exclusive, dlc)
    fixed = False
    if fix and fixes:
        backup = path.with_name(f"mods.json.probe-backup-{time.strftime('%Y%m%d-%H%M%S')}")
        shutil.copy2(path, backup)
        for f in fixes:
            f()
        path.write_bytes(_dump(data, crlf))
        fixed = True
        _prune_backups(path.parent)
    # Problems that remain after fixing (unregistered mod, not installed, ...) need a human.
    remaining, _ = inspect(data, ids, exclusive, dlc) if fixed else (problems, None)
    return {"ok": not remaining, "path": str(path), "dlc_id": dlc, "problems": problems,
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
    ap.add_argument("--dlc-id", help="DLC id the game checks (default: the one most mods use, else EXPANSION1_ID)")
    args = ap.parse_args(argv)
    if not args.mods_dir:
        ap.error("--mods-dir or ONI_MODS_DIR is required")
    result = ensure(args.mods_dir, args.id, args.exclusive, fix=args.command == "fix", dlc_id=args.dlc_id)
    print(json.dumps(result, indent=2, ensure_ascii=False))
    if args.command == "check":
        return 0 if not result["problems"] else 1
    return 0 if result["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
