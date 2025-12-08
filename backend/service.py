"""
Windows Service Manager for OwlSamba
Runs backend and frontend with system tray icon and embedded browser.
"""

import os
import shutil
import signal
import subprocess
import sys
import threading
import time
import logging
import webbrowser
from pathlib import Path
from typing import Dict, Optional
from datetime import datetime

from dotenv import load_dotenv
from PIL import Image, ImageDraw
import pystray

ROOT = Path(__file__).resolve().parent.parent
LOGS_DIR = ROOT / "logs"
ENV_FILE = ROOT / ".env"

load_dotenv(ENV_FILE)

API_PORT = int(os.getenv("API_PORT", "8000"))
UI_PORT = int(os.getenv("UI_PORT", "5173"))
SERVICE_LOG = LOGS_DIR / "service.log"

LOGS_DIR.mkdir(parents=True, exist_ok=True)

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    handlers=[
        logging.FileHandler(SERVICE_LOG, encoding="utf-8"),
        logging.StreamHandler()
    ]
)
logger = logging.getLogger(__name__)


def _kill_port_process(port: int) -> None:
    """Kill process using specified port on Windows."""
    if os.name != "nt":
        return
    
    try:
        result = subprocess.run(
            ["netstat", "-ano"],
            capture_output=True,
            text=True,
            check=False
        )
        for line in result.stdout.split('\n'):
            if f':{port}' in line and 'LISTENING' in line:
                parts = line.split()
                if len(parts) >= 5:
                    pid = parts[-1]
                    try:
                        subprocess.run(["taskkill", "/PID", pid, "/F"], check=False)
                        logger.info(f"Killed process on port {port} (PID: {pid})")
                    except Exception as e:
                        logger.debug(f"Failed to kill PID {pid}: {e}")
    except Exception as e:
        logger.debug(f"Error killing port {port}: {e}")


class ServiceManager:
    """Manages backend and frontend processes."""
    
    def __init__(self):
        self.processes: Dict[str, Optional[subprocess.Popen]] = {
            "backend": None,
            "frontend": None,
        }
        self.stop_event = threading.Event()
        self.lock = threading.Lock()
    
    def _start_backend(self) -> bool:
        """Start FastAPI backend with Uvicorn."""
        try:
            cmd = [
                sys.executable,
                "-m",
                "uvicorn",
                "backend.app:app",
                "--host",
                "0.0.0.0",
                "--port",
                str(API_PORT),
                "--log-level",
                "info"
            ]
            self.processes["backend"] = subprocess.Popen(
                cmd,
                cwd=ROOT,
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
            )
            logger.info(f"Backend started (PID: {self.processes['backend'].pid})")
            return True
        except Exception as e:
            logger.error(f"Failed to start backend: {e}")
            return False
    
    def _start_frontend(self) -> bool:
        """Start Vite frontend dev server."""
        npm = shutil.which("npm")
        if not npm:
            logger.warning("npm not found; skipping frontend startup")
            return False
        
        try:
            cmd = [npm, "run", "dev", "--", "--host", "0.0.0.0", "--port", str(UI_PORT)]
            self.processes["frontend"] = subprocess.Popen(
                cmd,
                cwd=ROOT / "frontend",
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
            )
            logger.info(f"Frontend started (PID: {self.processes['frontend'].pid})")
            return True
        except Exception as e:
            logger.error(f"Failed to start frontend: {e}")
            return False
    
    def _stop_process(self, name: str) -> None:
        """Stop a process gracefully."""
        proc = self.processes.get(name)
        if proc is None:
            return
        
        if proc.poll() is not None:
            logger.debug(f"{name} already stopped")
            return
        
        try:
            logger.info(f"Stopping {name} (PID: {proc.pid})")
            if os.name == "nt":
                try:
                    proc.send_signal(signal.CTRL_BREAK_EVENT)
                except Exception:
                    proc.terminate()
            else:
                proc.terminate()
            
            proc.wait(timeout=5)
            logger.info(f"{name} stopped successfully")
        except subprocess.TimeoutExpired:
            logger.warning(f"{name} did not stop gracefully, killing...")
            proc.kill()
            proc.wait()
        except Exception as e:
            logger.error(f"Error stopping {name}: {e}")
            proc.kill()
    
    def start(self) -> bool:
        with self.lock:
            logger.info("=" * 60)
            logger.info(f"OwlSamba Service Starting ({datetime.now().isoformat()})")
            logger.info("=" * 60)
            
            _kill_port_process(API_PORT)
            _kill_port_process(UI_PORT)
            time.sleep(1)
            
            if not self._start_backend():
                logger.error("Failed to start backend; aborting startup")
                self.stop()
                return False
            
            time.sleep(2)
            
            self._start_frontend()
            
            logger.info("All services started")
            return True
    
    def stop(self) -> None:
        with self.lock:
            logger.info("=" * 60)
            logger.info(f"OwlSamba Service Stopping ({datetime.now().isoformat()})")
            logger.info("=" * 60)
            
            for name in ["frontend", "backend"]:
                self._stop_process(name)
            
            logger.info("All services stopped")
            self.stop_event.set()
    
    def health_check(self) -> None:
        while not self.stop_event.is_set():
            try:
                with self.lock:
                    if self.processes["backend"] and self.processes["backend"].poll() is not None:
                        logger.error("Backend process died, attempting restart...")
                        self._start_backend()
                    
                    if self.processes["frontend"] and self.processes["frontend"].poll() is not None:
                        logger.warning("Frontend process died, attempting restart...")
                        self._start_frontend()
                
                time.sleep(5)
            except Exception as e:
                logger.error(f"Health check error: {e}")
                time.sleep(5)
    
    def run(self) -> None:
        if not self.start():
            logger.error("Failed to start service")
            return
        
        health_thread = threading.Thread(target=self.health_check, daemon=True)
        health_thread.start()
        
        def signal_handler(signum, frame):
            logger.info(f"Received signal {signum}, shutting down...")
            self.stop()
        
        signal.signal(signal.SIGTERM, signal_handler)
        signal.signal(signal.SIGINT, signal_handler)
        
        try:
            while not self.stop_event.is_set():
                time.sleep(1)
        except KeyboardInterrupt:
            logger.info("Keyboard interrupt received")
            self.stop()


def _create_shield_icon(size: int = 128) -> Image.Image:
    """Create a shield icon for the system tray."""
    stroke = max(2, size // 16)
    padding = stroke * 2
    img = Image.new("RGBA", (size, size), (255, 255, 255, 0))
    draw = ImageDraw.Draw(img)

    outline = [
        (padding, padding + size * 0.1),
        (size / 2, padding),
        (size - padding, padding + size * 0.1),
        (size - padding, size * 0.6),
        (size / 2, size - padding),
        (padding, size * 0.6),
    ]
    draw.line(outline + [outline[0]], fill=(15, 23, 42, 255), width=stroke, joint="curve")

    check = [
        (size * 0.34, size * 0.55),
        (size * 0.46, size * 0.68),
        (size * 0.68, size * 0.38),
    ]
    draw.line(check, fill=(52, 211, 153, 255), width=stroke, joint="curve")

    return img


def _open_browser(manager: 'ServiceManager', icon: pystray.Icon, item: pystray.MenuItem) -> None:
    """Open the dashboard using Edge in app mode (kiosk-like window)."""
    url = f"http://localhost:{UI_PORT}"
    logger.info(f"Opening dashboard at {url}")
    
    try:
        icon_path = ROOT / "frontend" / "public" / "shield-favicon.svg"
        
        edge_path = r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
        if os.path.exists(edge_path):
            subprocess.Popen([
                edge_path,
                "--app=" + url,
                "--new-window",
            ])
        else:
            webbrowser.open(url)
    except Exception as e:
        logger.error(f"Failed to open browser: {e}")
        webbrowser.open(url)


def _stop_service(manager: 'ServiceManager', icon: pystray.Icon, item: pystray.MenuItem) -> None:
    """Stop the service and exit."""
    logger.info("User requested service shutdown")
    icon.stop()
    manager.stop()


def _restart_service(manager: 'ServiceManager', icon: pystray.Icon, item: pystray.MenuItem) -> None:
    """Restart the service."""
    logger.info("User requested service restart")
    manager.stop()
    time.sleep(2)
    if manager.start():
        logger.info("Service restarted successfully")
    else:
        logger.error("Failed to restart service")


def run_with_tray(manager: 'ServiceManager') -> None:
    if not manager.start():
        logger.error("Failed to start service")
        return
    
    health_thread = threading.Thread(target=manager.health_check, daemon=True)
    health_thread.start()
    
    menu = pystray.Menu(
        pystray.MenuItem("Open Dashboard", lambda icon, item: _open_browser(manager, icon, item), default=True),
        pystray.MenuItem("Restart Service", lambda icon, item: _restart_service(manager, icon, item)),
        pystray.MenuItem("Exit", lambda icon, item: _stop_service(manager, icon, item)),
    )
    
    icon = pystray.Icon("OwlSamba", _create_shield_icon(), "OwlSamba", menu=menu)
    
    def signal_handler(signum, frame):
        logger.info(f"Received signal {signum}, shutting down...")
        icon.stop()
        manager.stop()
    
    signal.signal(signal.SIGTERM, signal_handler)
    signal.signal(signal.SIGINT, signal_handler)
    
    try:
        icon.run()
    except KeyboardInterrupt:
        logger.info("Keyboard interrupt received")
        manager.stop()


def main():
    manager = ServiceManager()
    run_with_tray(manager)
if __name__ == "__main__":
    main()
