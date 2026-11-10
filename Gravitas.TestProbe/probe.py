"""Gravitas.TestProbe: drive one ONI verification cycle from the shell and print a JSON result.

    python probe.py run            build -> fix mods.json -> launch -> wait ready -> check version -> /quit
    python probe.py run --save "name.sav"   also auto-load a save from saves/ and wait until it is playing
    python probe.py run --save X.sav --steps '[["POST","/time/advance",{"ticks":50}],["POST","/query",{"path":"GameClock.Instance.GetCycle"}]]'
    python probe.py run --project ../../MyMod --tests   test another mod: its harness.json gives build/required_mods/save
    python probe.py run --keep    same, but leave the game running (for manual poking)
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
        # its folder), required_mods, save, saves_dir, port, build_harness (default true), runs_dir.
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
        for key in ("required_mods", "save", "port"):
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
        mods = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], fix=True)
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
                          "-harness-out", str(run_dir / "screenshots"), *extra])
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
                # The kill interrupts mod loading; repair mods.json so the next run is not in safe mode.
                result["mods_json_after_kill"] = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], fix=True)
        if player_log.exists():
            shutil.copy2(player_log, run_dir / "Player.log")
            result["player_log"] = str(run_dir / "Player.log")
            # Exceptions matter even when the run itself passed.
            result["log_exceptions"] = extract_exceptions(run_dir / "Player.log")
            if not result["ok"]:
                result["crash"] = crash_info(run_dir / "Player.log", cfg["game_dir"], run_started)
        (run_dir / "result.json").write_text(json.dumps(result, indent=2, ensure_ascii=False), encoding="utf-8")
    return result


def main():
    sys.stdout.reconfigure(encoding="utf-8")  # the Windows console codec (cp932) cannot print everything the game logs
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest="cmd", required=True)
    r = sub.add_parser("run")
    r.add_argument("--project", help="harness.json of the mod under test (or its folder): its build commands, required_mods, "
                   "save and runs location are used on top of the harness build")
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
        if args.cmd == "run":
            tests = None
            if args.tests is not None:
                tests = json.loads(args.tests) if args.tests.lstrip().startswith("{") else {"pattern": args.tests}
            out = run_cycle(cfg, args.keep, args.save, json.loads(args.steps) if args.steps else None, tests)
        elif args.cmd == "fix-mods":
            out = mods_json.ensure(cfg["mods_dir"], cfg["required_mods"], fix=True)
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
