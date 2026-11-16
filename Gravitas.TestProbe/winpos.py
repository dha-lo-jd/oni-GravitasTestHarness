"""Keeps the game window out of the way of the person at the keyboard (Windows only, no dependencies).

Steam's -applaunch brings the game to the front when it starts. `place(mode)` moves its top-level windows without activating them:

  front       leave the window alone (default)
  background  send it to the bottom of the z-order, behind every other window, still on screen and rendering
  minimize    minimise without activating (the game may stop rendering; check that screenshots still work)
  offscreen   move it far outside the visible desktop, without activating

Call it repeatedly while waiting for the game: the splash window and the main window are different windows, and Unity may bring
the main one forward once loading finishes. Every call is cheap and idempotent.
"""
import ctypes
import subprocess
from ctypes import wintypes

MODES = ("front", "background", "minimize", "offscreen")

_user32 = ctypes.WinDLL("user32", use_last_error=True)
HWND_BOTTOM = wintypes.HWND(1)
SWP_NOSIZE, SWP_NOMOVE, SWP_NOACTIVATE = 0x0001, 0x0002, 0x0010
SW_SHOWMINNOACTIVE = 7
OFFSCREEN = -32000

_EnumProc = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)
_user32.SetWindowPos.argtypes = [wintypes.HWND, wintypes.HWND, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int, wintypes.UINT]
_user32.ShowWindow.argtypes = [wintypes.HWND, ctypes.c_int]
_user32.SetForegroundWindow.argtypes = [wintypes.HWND]
_user32.IsWindow.argtypes = [wintypes.HWND]


def game_pids(image="OxygenNotIncluded.exe"):
    raw = subprocess.run(["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV", "/NH"], capture_output=True).stdout or b""
    out = raw.decode("cp932", "replace")  # the console code page; only the PID and the image name matter
    pids = set()
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if len(parts) > 1 and parts[0].strip('"').lower() == image.lower() and parts[1].strip('"').isdigit():
            pids.add(int(parts[1].strip('"')))
    return pids


def windows_of(pids):
    found = []

    def visit(hwnd, _):
        pid = wintypes.DWORD()
        _user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if pid.value in pids and _user32.IsWindowVisible(hwnd) and not _user32.GetWindow(hwnd, 4):  # GW_OWNER: top-level only
            found.append(hwnd)
        return True

    _user32.EnumWindows(_EnumProc(visit), 0)
    return found


_previous = None


def remember_foreground():
    """Call just before launching the game: the window that has the person's focus now, to hand the focus back to."""
    global _previous
    _previous = _user32.GetForegroundWindow() or None


def _give_focus_back(game_hwnd):
    """Sending the active window to the bottom leaves it active, so keystrokes would still go to the hidden game.
    Re-activate the window the person was in. Windows only allows that from a process that recently had input, hence the
    Alt key tap, which is the usual way to satisfy it."""
    if _user32.GetForegroundWindow() != game_hwnd or not _previous or _previous == game_hwnd or not _user32.IsWindow(_previous):
        return
    _user32.keybd_event(0x12, 0, 0, 0)       # VK_MENU down
    _user32.keybd_event(0x12, 0, 2, 0)       # VK_MENU up (KEYEVENTF_KEYUP)
    _user32.SetForegroundWindow(_previous)


def place(mode):
    """Applies the mode to the game's windows. Returns how many windows were touched."""
    if mode in (None, "front"):
        return 0
    if mode not in MODES:
        raise ValueError(f"unknown window mode {mode!r}; one of {MODES}")
    wins = windows_of(game_pids())
    for hwnd in wins:
        if mode == "background":
            _user32.SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
            _give_focus_back(hwnd)
        elif mode == "minimize":
            _user32.ShowWindow(hwnd, SW_SHOWMINNOACTIVE)
        elif mode == "offscreen":
            _user32.SetWindowPos(hwnd, None, OFFSCREEN, OFFSCREEN, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE)
    return len(wins)
