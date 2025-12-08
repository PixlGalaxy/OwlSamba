"""Minimal tray launcher for OwlSamba backend and dashboard.

This script starts the FastAPI backend with Uvicorn and the Vite
frontend dev server, exposes a system tray icon (lucide-inspired
shield), lets you open the dashboard in a browser, and stops both
services through an Exit button.
"""

import os
import shutil
import signal
import subprocess
import sys
import threading
import webbrowser
from pathlib import Path
from typing import List, Optional

from dotenv import load_dotenv
from PIL import Image, ImageDraw
import pystray

ROOT = Path(__file__).resolve().parent.parent
ENV_FILE = ROOT / ".env"
load_dotenv(ENV_FILE)

API_PORT = int(os.getenv("API_PORT", "8000"))
UI_PORT = int(os.getenv("UI_PORT", "5173"))

ProcessList = List[Optional[subprocess.Popen]]


def _lucide_shield_icon(size: int = 128) -> Image.Image:
    """Draw a lucide-style shield check icon for the system tray."""

    stroke = max(2, size // 16)
    padding = stroke * 2
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(img)

    # Shield outline (lucide-like geometry)
    outline = [
        (padding, padding + size * 0.1),
        (size / 2, padding),
        (size - padding, padding + size * 0.1),
        (size - padding, size * 0.6),
        (size / 2, size - padding),
        (padding, size * 0.6),
    ]
    draw.line(outline + [outline[0]], fill=(15, 23, 42, 255), width=stroke, joint="curve")

    # Check mark
    check = [
        (size * 0.34, size * 0.55),
        (size * 0.46, size * 0.68),
        (size * 0.68, size * 0.38),
    ]
    draw.line(check, fill=(52, 211, 153, 255), width=stroke, joint="curve")

    return img


def _start_backend() -> Optional[subprocess.Popen]:
    cmd = [
        sys.executable,
        "-m",
        "uvicorn",
        "backend.app:app",
        "--host",
        "0.0.0.0",
        "--port",
        str(API_PORT),
    ]
    try:
        return subprocess.Popen(
            cmd,
            cwd=ROOT,
            start_new_session=True,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
        )
    except FileNotFoundError:
        print("Uvicorn is not installed; cannot start backend.")
        return None


def _start_frontend() -> Optional[subprocess.Popen]:
    npm = shutil.which("npm")
    if not npm:
        print("npm is not available; skipping frontend startup.")
        return None
    cmd = [npm, "run", "dev", "--", "--host", "0.0.0.0", "--port", str(UI_PORT)]
    try:
        return subprocess.Popen(
            cmd,
            cwd=ROOT / "frontend",
            start_new_session=True,
            creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0,
        )
    except FileNotFoundError:
        print("Unable to launch frontend dev server.")
        return None


def _stop_process(proc: Optional[subprocess.Popen]) -> None:
    if proc is None:
        return
    if proc.poll() is not None:
        return
    try:
        if os.name != "nt":
            os.killpg(proc.pid, signal.SIGTERM)
        else:
            proc.terminate()
        proc.wait(timeout=5)
    except subprocess.TimeoutExpired:
        if os.name != "nt":
            os.killpg(proc.pid, signal.SIGKILL)
        else:
            proc.kill()
    except Exception:
        proc.kill()


def _open_dashboard(_: pystray.Icon, __: pystray.MenuItem) -> None:
    webbrowser.open(f"http://localhost:{UI_PORT}")


def _exit_app(icon: pystray.Icon, _: pystray.MenuItem, procs: ProcessList, stop_event: threading.Event) -> None:
    stop_event.set()
    icon.stop()
    for proc in procs:
        _stop_process(proc)


def run_tray() -> None:
    backend_proc = _start_backend()
    frontend_proc = _start_frontend()

    procs: ProcessList = [backend_proc, frontend_proc]
    stop_event = threading.Event()

    menu = pystray.Menu(
        pystray.MenuItem("Open dashboard", _open_dashboard, default=True),
        pystray.MenuItem(
            "Exit",
            lambda icon, item: _exit_app(icon, item, procs, stop_event),
        ),
    )

    icon = pystray.Icon("OwlSamba", _lucide_shield_icon(), "OwlSamba", menu=menu)
    icon.run()

    stop_event.set()
    for proc in procs:
        _stop_process(proc)


if __name__ == "__main__":
    run_tray()
