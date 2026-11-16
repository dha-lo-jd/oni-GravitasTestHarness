"""One run that uses the game at a time, and the person at the keyboard always goes first.

A probe run rewrites the real mods.json and starts ONI, so two runs (or a run and the person starting the game) must not overlap.
The lock is a file next to the probe: {owner, pid, since, background}. A lock whose pid is gone is stale and is taken over.

  owner       who runs: "main" (default, a run someone asked for) or e.g. "regress" (the unattended sweep, tools/regress.py)
  background  True for unattended runs. They never wait for the lock, and a main run that finds one holding it leaves a yield
              request (.game.yield) and waits for it to finish; tools/regress.py looks for that file between mods and stops.

Environment: GRAVITAS_LOCK_OWNER, GRAVITAS_LOCK_BACKGROUND=1, GRAVITAS_LOCK_WAIT (seconds a main run waits; default 300).
"""
import ctypes
import json
import os
import subprocess
import time
from contextlib import contextmanager
from ctypes import wintypes
from pathlib import Path

HERE = Path(__file__).resolve().parent
LOCK = HERE.parent / ".game.lock"
YIELD = HERE.parent / ".game.yield"


class Busy(Exception):
    pass


def pid_alive(pid):
    raw = subprocess.run(["tasklist", "/FI", f"PID eq {int(pid)}", "/FO", "CSV", "/NH"], capture_output=True).stdout or b""
    return f'"{int(pid)}"' in raw.decode("cp932", "replace")


def read():
    try:
        return json.loads(LOCK.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return None


def yield_requested():
    return YIELD.exists()


def acquire(owner="main", background=False, wait_s=0):
    deadline = time.time() + wait_s
    while True:
        cur = read()
        if cur is not None and not pid_alive(cur.get("pid", 0)):
            LOCK.unlink(missing_ok=True)  # stale: the holder died
            continue
        if cur is None and LOCK.exists():  # unreadable file: another process is writing it right now
            time.sleep(0.2)
            continue
        if cur is None:
            try:
                fd = os.open(LOCK, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            except FileExistsError:
                continue
            with os.fdopen(fd, "w", encoding="utf-8") as f:
                json.dump({"owner": owner, "pid": os.getpid(), "since": time.strftime("%Y-%m-%d %H:%M:%S"),
                           "background": bool(background)}, f)
            if not background:
                YIELD.unlink(missing_ok=True)
            return
        if cur.get("pid") == os.getpid():
            return
        if not background and cur.get("background"):
            YIELD.write_text(json.dumps({"by": owner, "at": time.strftime("%Y-%m-%d %H:%M:%S")}), encoding="utf-8")
        if time.time() >= deadline:
            raise Busy(f"the game is in use by '{cur.get('owner')}' (pid {cur.get('pid')}, since {cur.get('since')})"
                       + (" - an unattended run; a yield request was left, retry in a few minutes" if cur.get("background") else ""))
        time.sleep(2)


def release():
    cur = read()
    if cur is not None and cur.get("pid") == os.getpid():
        LOCK.unlink(missing_ok=True)


@contextmanager
def hold(owner=None, background=None, wait_s=None):
    owner = owner or os.environ.get("GRAVITAS_LOCK_OWNER", "main")
    background = (os.environ.get("GRAVITAS_LOCK_BACKGROUND") == "1") if background is None else background
    if wait_s is None:
        wait_s = 0 if background else float(os.environ.get("GRAVITAS_LOCK_WAIT", "300"))
    acquire(owner, background, wait_s)
    try:
        yield
    finally:
        release()


def idle_seconds():
    """Seconds since the last keyboard or mouse input of the logged-in person."""
    class LASTINPUTINFO(ctypes.Structure):
        _fields_ = [("cbSize", wintypes.UINT), ("dwTime", wintypes.DWORD)]
    info = LASTINPUTINFO()
    info.cbSize = ctypes.sizeof(info)
    if not ctypes.windll.user32.GetLastInputInfo(ctypes.byref(info)):
        return 0.0
    return (ctypes.windll.kernel32.GetTickCount() - info.dwTime) / 1000.0
