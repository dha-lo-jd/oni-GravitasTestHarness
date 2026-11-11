"""Gravitas.TestProbe: drive one ONI verification cycle from the shell and print a JSON result.

    python probe.py run            build -> fix mods.json -> launch -> wait ready -> check version -> /quit
    python probe.py run --save "name.sav"   also auto-load a save from saves/ and wait until it is playing
    python probe.py run --save X.sav --steps '[["POST","/time/advance",{"ticks":50}],["POST","/query",{"path":"GameClock.Instance.GetCycle"}]]'
    python probe.py run --project ../../MyMod --tests   test another mod: its harness.json gives build/required_mods/save
    python probe.py run --keep    same, but leave the game running (for manual poking)
    python probe.py run --setup    first-time registration of a new mod: build -> launch (mods.json untouched) -> wait until
                                   ONI has registered the mods in mods.json -> close. Run once before the first normal run
    python probe.py fix-mods       only check/repair mods.json
    python probe.py http GET /ping call an endpoint of an already running game

Settings come from probe.config.json (git-ignored; see probe.config.example.json).
Each run writes artifacts to runs/<timestamp>/ (result.json, Player.log, build.log).
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

import mods_json

HERE = Path(__file__).resolve().parent
REPO = HERE.parent
DEFAULT_PLAYER_LOG = Path.home() / "AppData/LocalLow/Klei/Oxygen Not Included/Player.log"


class ProbeError(Exception):
    def __init__(self, stage, message):
        super().__init__(message)
        self.stage, self.message = stage, message


def load_config(project=None):
    cfg = {"port": 18080, "required_mods": [], "build": [], "launch_args": [],
           "ready_timeout_s": 240, "quit_timeout_s": 60, "launch_timeout_s": 90, "load_timeout_s": 240, "step_timeout_s": 300, "save": None, "player_log": None,
           "steam_exe": "C:/Program Files (x86)/Steam/steam.exe", "app_id": 457140}
    path = HERE / "probe.config.json"
    if not path.exists():
        raise ProbeError("config", f"{path} not found (copy probe.config.example.json)")
    cfg.update(json.loads(path.read_text(encoding="utf-8")))
    for key in ("game_dir", "mods_dir"):
        if not cfg.get(key):
            raise ProbeError("config", f"'{key}' is required in probe.config.json")
    cfg["runs_dir"] = HERE / "runs"
    cfg["saves_dirs"] = [REPO / "saves"]
    cfg["project_build"] = []
    cfg["project_dir"] = None
    if project:
        # Project file of the mod under test (conventionally <mod repo>/harness.json). Keys: build (commands run in
        # its folder), required_mods, exclusive (disable every other mod), save, saves_dir, port, build_harness (default true), runs_dir.
        ppath = Path(project).resolve()
        if ppath.is_dir():
            ppath = ppath / "harness.json"
        if not ppath.exists():
            raise ProbeError("config", f"project file not found: {ppath}")
        try:
            proj = json.loads(ppath.read_text(encoding="utf-8"))
        except ValueError as e:
            raise ProbeError("config", f"{ppath} is not valid JSON: {e}")
        pdir = ppath.parent
        cfg["project_dir"] = pdir
        cfg["project_build"] = proj.get("build", [])
        if proj.get("build_harness", True) is False:
            cfg["build"] = []
        for key in ("required_mods", "save", "port", "exclusive"):
            if key in proj:
                cfg[key] = proj[key]
        cfg["runs_dir"] = (pdir / proj["runs_dir"]) if proj.get("runs_dir") else pdir / ".harness-runs"
        if proj.get("saves_dir"):
            cfg["saves_dirs"].insert(0, pdir / proj["saves_dir"])
    return cfg


def expected_versions():
    src = (REPO / "Gravitas.TestHarness" / "TestHarnessMod.cs").read_text(encoding="utf-8")
    return (re.search(r'Version = "([^"]+)"', src).group(1),
            int(re.search(r"ProtocolVersion = (\d+)", src).group(1)))


def http(cfg, method, path, body=None, timeout=10):
    # Git Bash (MSYS) rewrites a leading "/ping" into "C:/Program Files/Git/ping"; undo that.
    path = re.sub(r"^[A-Za-z]:[/\\]Program Files[/\\]Git(?=/)", "", path.replace("\\", "/"))
    data = json.dumps(body).encode() if body is not None else (b"" if method == "POST" else None)
    req = urllib.request.Request(f"http://127.0.0.1:{cfg['port']}{path}", data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return json.loads(r.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        return json.loads(e.read().decode("utf-8"))


def game_running():
    out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq OxygenNotIncluded.exe", "/NH"],
                         capture_output=True, text=True).stdout
    return "OxygenNotIncluded.exe" in out


def build(cfg, run_dir):
    log = run_dir / "build.log"
    with log.open("w", encoding="utf-8") as f:
        steps = [(c, HERE) for c in cfg["build"]] + [(c, cfg["project_dir"]) for c in cfg["project_build"]]
        for cmd, cwd in steps:
            f.write(f"> ({cwd}) {cmd}\n")
            f.flush()
            p = subprocess.run(cmd, shell=True, cwd=cwd, stdout=f, stderr=subprocess.STDOUT)
            if p.returncode != 0:
                tail = "\n".join(log.read_text(encoding="utf-8", errors="replace").splitlines()[-15:])
                raise ProbeError("build", f"'{cmd}' failed (exit {p.returncode}). See {log}\n{tail}")


STACK_RE = re.compile(r"^\s+at |^\(Filename:|^\s*--- End of")
EXC_START_RE = re.compile(r"\w*Exception|\[ERROR\]|Fatal")
TS_RE = re.compile(r"^\[\d\d:\d\d:\d\d\.\d+\] \[\d+\] ")


def extract_exceptions(log_path, limit=20, max_lines=25):
    """Exception / error blocks from Player.log: the first line plus its stack trace, de-duplicated."""
    if not log_path.exists():
        return []
    lines = log_path.read_text(encoding="utf-8", errors="replace").splitlines()
    blocks, seen, i = [], {}, 0
    while i < len(lines):
        line = lines[i]
        if EXC_START_RE.search(line) and not STACK_RE.match(line):
            if any("Thread was being aborted" in l for l in lines[i:i + 4]):
                i += 1  # background loader thread cut short by a normal shutdown, not a fault
                continue
            block = [line]
            i += 1
            while i < len(lines) and len(block) < max_lines and (STACK_RE.match(lines[i]) or not TS_RE.match(lines[i]) and lines[i].strip() and EXC_START_RE.search(lines[i])):
                block.append(lines[i])
                i += 1
            key = TS_RE.sub("", block[0]).strip()
            if key in seen:
                blocks[seen[key]]["count"] += 1
            elif len(blocks) < limit:
                seen[key] = len(blocks)
                blocks.append({"count": 1, "text": "\n".join(block)})
            continue
        i += 1
    return blocks


def crash_info(player_log, game_dir, since):
    """Evidence of a crash: new dump files, mod safe mode, last log lines."""
    info = {}
    dumps = [str(p) for p in Path(game_dir).glob("*.dmp") if p.stat().st_mtime >= since]
    if dumps:
        info["dump_files"] = dumps
    if player_log.exists():
        lines = player_log.read_text(encoding="utf-8", errors="replace").splitlines()
        info["safe_mode"] = any("Entering mod safe mode" in l for l in lines)
        info["log_tail"] = lines[-30:]
    return info


def run_cycle(cfg, keep=False, save=None, steps=None, tests=None):
    run_dir = cfg["runs_dir"] / time.strftime("%Y%m%d-%H%M%S")
    run_dir.mkdir(parents=True)
    result = {"ok": False, "run_dir": str(run_dir), "stage": "start"}
    player_log = Path(cfg["player_log"]) if cfg.get("player_log") else DEFAULT_PLAYER_LOG
    launched = False
    original_mods = None
    run_started = time.time()
    try:
        if game_running():
            raise ProbeError("precheck", "OxygenNotIncluded.exe is already running; close it first")
        # Keep only the latest few run folders.
        for old in sorted(cfg["runs_dir"].iterdir())[:-10]:
            shutil.rmtree(old, ignore_errors=True)

        result["stage"] = "build"
        build(cfg, run_dir)

        result["stage"] = "mods.json"
        # The run needs the harness and the mods under test enabled, but the user's own set must come back afterwards.
        original_mods = mods_json.snapshot(cfg["mods_dir"])
        mods = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], cfg.get("exclusive", False), fix=True, dlc_id=cfg.get("dlc_id"))
        result["mods_json"] = mods
        if not mods["ok"]:
            raise ProbeError("mods.json", "; ".join(mods["remaining"]))

        result["stage"] = "launch"
        if player_log.exists():
            player_log.unlink()
        # Launching the exe directly makes Steam show a confirmation prompt; go through Steam instead.
        # (Steam does not pass our environment on, so the port travels as a launch argument.)
        steam = Path(cfg["steam_exe"])
        if not steam.exists():
            raise ProbeError("launch", f"steam_exe not found: {steam}")
        launched = True
        save = save or cfg.get("save")
        save_path = None
        if save:
            if Path(save).is_absolute():
                save_path = Path(save)
            else:
                save_path = next((d / save for d in cfg["saves_dirs"] if (d / save).exists()), cfg["saves_dirs"][0] / save)
            if not save_path.exists():
                raise ProbeError("launch", f"save not found: {save_path}")
        extra = ["-harness-load", str(save_path)] if save_path else []
        subprocess.Popen([str(steam), "-applaunch", str(cfg["app_id"]), *cfg["launch_args"],
                          "-harness-port", str(cfg["port"]),
                          "-harness-out", str(run_dir / "screenshots"), *extra],
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        started = time.time()
        while not game_running():
            if time.time() - started > cfg["launch_timeout_s"]:
                raise ProbeError("launch", f"OxygenNotIncluded.exe did not start within {cfg['launch_timeout_s']} s "
                                           "(Steam prompt? not logged in?)")
            time.sleep(0.5)

        result["stage"] = "wait-ready"
        ping = None
        while time.time() - started < cfg["ready_timeout_s"]:
            if not game_running():
                raise ProbeError("wait-ready", "game process exited before the harness answered")
            try:
                ping = http(cfg, "GET", "/ping", timeout=3)
                if ping.get("ok"):
                    break
            except (urllib.error.URLError, OSError, ValueError):
                pass
            time.sleep(1)
        else:
            raise ProbeError("wait-ready", f"no /ping response within {cfg['ready_timeout_s']} s")
        result["ready_after_s"] = round(time.time() - started, 1)
        result["ping"] = ping["data"]

        result["stage"] = "version"
        ver = http(cfg, "GET", "/version")["data"]
        exp_ver, exp_proto = expected_versions()
        result["version"] = ver
        if ver["version"] != exp_ver or ver["protocol"] != exp_proto:
            raise ProbeError("version", f"harness mismatch: game has {ver}, expected "
                                        f"version={exp_ver} protocol={exp_proto} (stale deployment?)")

        if save_path:
            result["stage"] = "load-save"
            load_started = time.time()
            status = None
            while time.time() - load_started < cfg["load_timeout_s"]:
                if not game_running():
                    raise ProbeError("load-save", "game process exited while loading the save")
                try:
                    status = http(cfg, "GET", "/status", timeout=5)["data"]
                except (urllib.error.URLError, OSError, ValueError, KeyError):
                    status = None
                if status and status["ready"]:
                    break
                time.sleep(1)
            else:
                raise ProbeError("load-save", f"save not ready within {cfg['load_timeout_s']} s (last status: {status})")
            result["load_after_s"] = round(time.time() - load_started, 1)
            result["status"] = status

        if steps:
            result["stage"] = "steps"
            result["steps"] = []
            for method, path, *rest in steps:
                reply = http(cfg, method.upper(), path, rest[0] if rest else None, timeout=cfg["step_timeout_s"])
                result["steps"].append({"request": [method, path, *rest], "response": reply})
                if not reply.get("ok"):
                    raise ProbeError("steps", f"{method} {path} failed: {reply.get('error', {}).get('message')}")

        if tests is not None:
            result["stage"] = "tests"
            reply = http(cfg, "POST", "/tests/run", tests, timeout=cfg["step_timeout_s"] * 6)
            if not reply.get("ok"):
                raise ProbeError("tests", f"/tests/run failed: {reply.get('error', {}).get('message')}")
            data = reply["data"]
            result["tests"] = data
            if data["total"] == 0:
                raise ProbeError("tests", "no test matched the selection")
            if data["failed"] or data["errors"]:
                bad = [f"{r['id']}: {r.get('message')}" for r in data["results"] if r["status"] in ("failed", "error", "timeout")]
                raise ProbeError("tests", f"{data['failed']} failed, {data['errors']} errors: " + "; ".join(bad))

        if keep:
            result.update(ok=True, stage="done", note="game left running (--keep)")
            return result

        result["stage"] = "quit"
        http(cfg, "POST", "/quit")
        deadline = time.time() + cfg["quit_timeout_s"]
        while game_running() and time.time() < deadline:
            time.sleep(0.5)
        if game_running():
            raise ProbeError("quit", f"game still running {cfg['quit_timeout_s']} s after /quit")
        result.update(ok=True, stage="done")
    except ProbeError as e:
        result.update(ok=False, stage=e.stage, error=e.message)
    except Exception as e:  # keep the JSON contract even for unexpected failures
        result.update(ok=False, error=f"{type(e).__name__}: {e}")
    finally:
        if launched and not keep and game_running():
            # Never kill a game that is still loading unless we must: that makes ONI boot into mod safe mode.
            try:
                http(cfg, "POST", "/quit", timeout=3)
            except (urllib.error.URLError, OSError, ValueError):
                pass
            deadline = time.time() + 20
            while game_running() and time.time() < deadline:
                time.sleep(0.5)
            if game_running():
                subprocess.run(["taskkill", "/F", "/IM", "OxygenNotIncluded.exe"], capture_output=True)
                time.sleep(2)
                result["killed"] = True
        # Put the user's mods.json back (also clears what a kill during mod loading leaves behind, so the next
        # start is not in mod safe mode). Only once the game is gone: ONI rewrites the file on exit.
        if original_mods is not None and not keep:
            if game_running():
                result["mods_json_restored"] = False
                result["mods_json_note"] = "game still running; mods.json not restored"
            else:
                result["mods_json_restored"] = True
                result["mods_json_changed_by_game_or_probe"] = mods_json.restore(cfg["mods_dir"], original_mods)
        elif keep and original_mods is not None:
            result["mods_json_note"] = "game left running (--keep): mods.json still has the probe's changes; run 'fix-mods' or restore the .probe-backup-* file after quitting"
        if player_log.exists():
            shutil.copy2(player_log, run_dir / "Player.log")
            result["player_log"] = str(run_dir / "Player.log")
            # Exceptions matter even when the run itself passed.
            result["log_exceptions"] = extract_exceptions(run_dir / "Player.log")
            if not result["ok"]:
                result["crash"] = crash_info(run_dir / "Player.log", cfg["game_dir"], run_started)
        (run_dir / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    return result


def registered_ids(mods_dir):
    """staticIDs ONI has registered in mods.json (empty if the file is missing or unreadable)."""
    try:
        data = json.loads(mods_json.mods_json_path(mods_dir).read_text(encoding="utf-8-sig"))
        return {m.get("staticID") for m in data.get("mods", [])}
    except (OSError, ValueError):
        return set()


def setup_cycle(cfg):
    """First-time registration of new mod DLLs: build, start the game WITHOUT touching mods.json, wait until ONI has
    registered the mods in mods.json, then close the game. Writing mods.json before ONI has seen a mod crashes the start."""
    run_dir = cfg["runs_dir"] / time.strftime("%Y%m%d-%H%M%S-setup")
    run_dir.mkdir(parents=True)
    result = {"ok": False, "run_dir": str(run_dir), "stage": "start", "mode": "setup"}
    player_log = Path(cfg["player_log"]) if cfg.get("player_log") else DEFAULT_PLAYER_LOG
    expected = set(cfg["required_mods"]) | {mods_json.HARNESS_ID}
    launched = False
    try:
        if game_running():
            raise ProbeError("precheck", "OxygenNotIncluded.exe is already running; close it first")
        result["stage"] = "build"
        build(cfg, run_dir)

        result["stage"] = "launch"
        before = registered_ids(cfg["mods_dir"])
        result["registered_before"] = sorted(expected & before)
        if player_log.exists():
            player_log.unlink()
        steam = Path(cfg["steam_exe"])
        if not steam.exists():
            raise ProbeError("launch", f"steam_exe not found: {steam}")
        launched = True
        subprocess.Popen([str(steam), "-applaunch", str(cfg["app_id"]), *cfg["launch_args"],
                          "-harness-port", str(cfg["port"]), "-harness-out", str(run_dir / "screenshots")],
                         stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        started = time.time()
        while not game_running():
            if time.time() - started > cfg["launch_timeout_s"]:
                raise ProbeError("launch", f"OxygenNotIncluded.exe did not start within {cfg['launch_timeout_s']} s")
            time.sleep(0.5)

        # Registration shows up as new entries in mods.json (ONI writes the file after it has scanned the mods folder).
        result["stage"] = "wait-registered"
        while True:
            if not game_running():
                raise ProbeError("wait-registered", "game process exited before the mods were registered")
            missing = expected - registered_ids(cfg["mods_dir"])
            if not missing:
                break
            if time.time() - started > cfg["ready_timeout_s"]:
                raise ProbeError("wait-registered", f"not registered in mods.json within {cfg['ready_timeout_s']} s: "
                                                    f"{sorted(missing)} (wrong folder or staticID? is mod_info.yaml in place?)")
            time.sleep(1)
        result["registered_after_s"] = round(time.time() - started, 1)

        # Do not close the game while it is still loading mods (that can leave ONI in mod safe mode): wait until the
        # harness reports the main menu, or, if the harness is not loaded (first time), until Player.log has been quiet.
        result["stage"] = "wait-boot"
        while True:
            if not game_running():
                raise ProbeError("wait-boot", "game process exited during start-up")
            try:
                st = http(cfg, "GET", "/status", timeout=3)
                if st.get("ok") and st["data"].get("screen") == "main_menu":
                    break
            except (urllib.error.URLError, OSError, ValueError, KeyError):
                pass
            try:
                if time.time() - player_log.stat().st_mtime > 15:
                    break
            except OSError:
                pass
            if time.time() - started > cfg["ready_timeout_s"]:
                raise ProbeError("wait-boot", f"start-up not finished within {cfg['ready_timeout_s']} s")
            time.sleep(1)
        result["booted_after_s"] = round(time.time() - started, 1)
        time.sleep(3)

        result["stage"] = "quit"
        # Ask the game to quit: through the harness if it answers, otherwise a window close request (taskkill without
        # /F is the normal quit path, so ONI can write mods.json).
        try:
            http(cfg, "POST", "/quit", timeout=3)
        except (urllib.error.URLError, OSError, ValueError):
            subprocess.run(["taskkill", "/IM", "OxygenNotIncluded.exe"], capture_output=True)
        deadline = time.time() + cfg["quit_timeout_s"]
        while game_running() and time.time() < deadline:
            time.sleep(0.5)
        if game_running():
            raise ProbeError("quit", f"game still running {cfg['quit_timeout_s']} s after the close request")
        result.update(ok=True, stage="done", registered=sorted(expected & registered_ids(cfg["mods_dir"])),
                      note="mods.json was not modified by the probe; run the probe normally from here")
    except ProbeError as e:
        result.update(ok=False, stage=e.stage, error=e.message)
    except Exception as e:
        result.update(ok=False, error=f"{type(e).__name__}: {e}")
    finally:
        if launched and game_running():
            result.setdefault("note", "game still running; close it before the next step")
        if player_log.exists():
            shutil.copy2(player_log, run_dir / "Player.log")
            result["player_log"] = str(run_dir / "Player.log")
        (run_dir / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    return result


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # the Windows console codec (cp932) cannot print everything the game logs
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--project", help="harness.json of the mod under test (or its folder): its build commands, required_mods, "
                   "save and runs location are used on top of the harness build")
    r.add_argument("--setup", action="store_true",
                   help="first-time registration: build, start the game WITHOUT touching mods.json, wait until the mods are "
                        "registered in mods.json, then close it (no save, no tests). Run this before the first normal run "
                        "after placing a new mod's DLL")
    r.add_argument("--keep", action="store_true")
    r.add_argument("--steps", help="JSON list of [method, path, body?] run in order once the save is playing")
    r.add_argument("--save", help="save to auto-load (file name in saves/, or a path); default: config 'save'")
    r.add_argument("--tests", nargs="?", const="{}", help="run in-game tests after the steps: a JSON body for /tests/run "
                   "(names/pattern/tags/timeout_s), a pattern string, or nothing for all non-manual tests")
    sub.add_parser("fix-mods")
    h = sub.add_parser("http")
    h.add_argument("method")
    h.add_argument("path")
    h.add_argument("body", nargs="?")
    args = ap.parse_args()
    try:
        cfg = load_config(getattr(args, "project", None))
        if args.cmd == "run" and args.setup:
            out = setup_cycle(cfg)
        elif args.cmd == "run":
            tests = None
            if args.tests is not None:
                tests = json.loads(args.tests) if args.tests.lstrip().startswith("{") else {"pattern": args.tests}
            out = run_cycle(cfg, args.keep, args.save, json.loads(args.steps) if args.steps else None, tests)
        elif args.cmd == "fix-mods":
            out = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], cfg.get("exclusive", False), fix=True, dlc_id=cfg.get("dlc_id"))
        else:
            out = http(cfg, args.method.upper(), args.path, json.loads(args.body) if args.body else None)
    except ProbeError as e:
        out = {"ok": False, "stage": e.stage, "error": e.message}
    except (urllib.error.URLError, OSError) as e:
        out = {"ok": False, "stage": "http", "error": f"cannot reach the harness (is the game running?): {e}"}
    print(json.dumps(out, indent=2, ensure_ascii=False))
    return 0 if out.get("ok") else 1


if __name__ == "__main__":
    sys.exit(main())
