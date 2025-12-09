"""
OwlSamba Launcher with PyQt5 UI
Dependency installation and update checking
"""

import sys
import subprocess
import os
from pathlib import Path
import threading
import time
from PyQt5.QtWidgets import (QApplication, QMainWindow, QWidget, QVBoxLayout, 
                             QLabel, QProgressBar, QPushButton, QMessageBox)
from PyQt5.QtCore import Qt, pyqtSignal, QObject, QThread
from PyQt5.QtGui import QFont

# DEBUG MODE: Set to True to skip launching service.pyw
DEBUG_MODE = False

ROOT = Path(__file__).resolve().parent
BACKEND_DIR = ROOT / "backend"
FRONTEND_DIR = ROOT / "frontend"
UPDATE_DIR = ROOT / "update"

# Map package names to their import names
PACKAGE_TO_IMPORT = {
    "fastapi": "fastapi",
    "uvicorn": "uvicorn",
    "python-dotenv": "dotenv",
    "pyqt5": "PyQt5",
    "bcrypt": "bcrypt",
    "slowapi": "slowapi",
    "tendo": "tendo",
    "Pillow": "PIL",
    "requests": "requests",
    "packaging": "packaging",
}

sys.path.insert(0, str(UPDATE_DIR))


class UpdateChecker(QObject):
    """Check for updates from GitHub."""
    update_found = pyqtSignal(dict)
    check_complete = pyqtSignal()
    
    def check(self):
        try:
            from update.checker import check_for_updates
            result = check_for_updates()
            if result.get('has_update'):
                self.update_found.emit(result)
        except Exception:
            pass
        finally:
            self.check_complete.emit()


class InstallWorker(QObject):
    """Worker thread for installation tasks."""
    python_progress = pyqtSignal(int)
    node_progress = pyqtSignal(int)
    finished = pyqtSignal(bool)
    
    def __init__(self, install_type):
        super().__init__()
        self.install_type = install_type
    
    def run(self):
        try:
            if self.install_type == "python":
                self._install_python()
            else:
                self._install_node()
        except Exception:
            self.finished.emit(False)
    
    def _install_python(self):
        """Install Python requirements - only missing packages."""
        self.python_progress.emit(10)
        
        requirements_file = BACKEND_DIR / "requirements.txt"
        if not requirements_file.exists():
            self.finished.emit(False)
            return
        
        try:
            # Parse requirements.txt
            requirements = self._parse_requirements(requirements_file)
            if not requirements:
                self.finished.emit(False)
                return
            
            # Check which packages are missing
            missing = self._check_missing_packages(requirements)
            
            if not missing:
                # All packages already installed
                self.python_progress.emit(100)
                self.finished.emit(True)
                return
            
            # Install missing packages one by one
            progress_step = 80 / len(missing)  # 10-90% for installation
            current_progress = 10
            
            for package in missing:
                process = subprocess.Popen(
                    [sys.executable, "-m", "pip", "install", package],
                    stdout=subprocess.PIPE,
                    stderr=subprocess.PIPE,
                    text=True
                )
                
                process.wait(timeout=60)
                
                if process.returncode != 0:
                    self.finished.emit(False)
                    return
                
                current_progress += progress_step
                self.python_progress.emit(int(current_progress))
            
            self.python_progress.emit(100)
            self.finished.emit(True)
        
        except Exception:
            self.finished.emit(False)
    
    def _parse_requirements(self, requirements_file):
        """Parse requirements.txt and return list of package names."""
        try:
            packages = []
            with open(requirements_file, 'r') as f:
                for line in f:
                    line = line.strip()
                    if line and not line.startswith('#'):
                        # Extract package name (before version specifiers)
                        pkg = line.split('==')[0].split('>=')[0].split('<=')[0].split('>')[0].split('<')[0].split('!=')[0].strip()
                        packages.append(pkg)
            return packages
        except Exception:
            return []
    
    def _check_missing_packages(self, packages):
        """Check which packages from list are not installed."""
        missing = []
        for package in packages:
            # Get the import name from mapping, or derive it
            import_name = PACKAGE_TO_IMPORT.get(package, package.replace('-', '_').lower())
            try:
                __import__(import_name)
            except ImportError:
                missing.append(package)
        return missing
    
    def _install_node(self):
        """Install Node modules - only if needed."""
        self.node_progress.emit(10)
        
        package_json = FRONTEND_DIR / "package.json"
        node_modules = FRONTEND_DIR / "node_modules"
        
        if not package_json.exists():
            self.finished.emit(False)
            return
        
        try:
            npm = subprocess.run(
                ["where", "npm"],
                capture_output=True,
                text=True,
                timeout=5
            )
            
            if npm.returncode != 0:
                self.finished.emit(False)
                return
            
            self.node_progress.emit(20)
            
            # Check if node_modules exists and seems complete
            needs_install = not self._check_node_modules_valid(node_modules, package_json)
            
            if not needs_install:
                # node_modules already installed and valid
                self.node_progress.emit(100)
                self.finished.emit(True)
                return
            
            # Install/update node modules
            process = subprocess.Popen(
                ["npm", "install"],
                cwd=FRONTEND_DIR,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True
            )
            
            # Simulate progress while waiting
            progress = 20
            while process.poll() is None:
                time.sleep(1)
                progress += 3
                if progress < 95:
                    self.node_progress.emit(progress)
            
            if process.returncode == 0:
                self.node_progress.emit(100)
                self.finished.emit(True)
            else:
                self.finished.emit(False)
        
        except subprocess.TimeoutExpired:
            self.finished.emit(False)
        except Exception:
            self.finished.emit(False)
    
    def _check_node_modules_valid(self, node_modules_dir, package_json):
        """Check if node_modules exists and has expected structure."""
        try:
            # If node_modules doesn't exist, need to install
            if not node_modules_dir.exists():
                return False
            
            # If .bin directory doesn't exist, need to install
            if not (node_modules_dir / ".bin").exists():
                return False
            
            # Check if package-lock.json is older than package.json (modified)
            package_lock = FRONTEND_DIR / "package-lock.json"
            if package_lock.exists():
                if package_json.stat().st_mtime > package_lock.stat().st_mtime:
                    return False
            
            return True
        except Exception:
            return False


class LauncherWindow(QMainWindow):
    """Main launcher window."""
    
    def __init__(self):
        super().__init__()
        self.python_done = False
        self.node_done = False
        self.python_success = False
        self.node_success = False
        self.update_info = None
        self.installing_update = False
        self.init_ui()
        self.check_updates()
    
    def init_ui(self):
        """Initialize UI components."""
        self.setWindowTitle("OwlSamba")
        self.setGeometry(100, 100, 500, 300)
        self.setStyleSheet(self._get_stylesheet())
        
        central_widget = QWidget()
        central_widget.setStyleSheet("background-color: #1a1a2e;")
        self.setCentralWidget(central_widget)
        
        layout = QVBoxLayout()
        layout.setContentsMargins(40, 30, 40, 30)
        layout.setSpacing(15)
        
        title = QLabel("OwlSamba")
        title_font = QFont()
        title_font.setPointSize(16)
        title_font.setBold(True)
        title.setFont(title_font)
        title.setStyleSheet("color: #ffffff;")
        layout.addWidget(title)
        
        self.status_label = QLabel("Checking for updates...")
        status_font = QFont()
        status_font.setPointSize(10)
        self.status_label.setFont(status_font)
        self.status_label.setStyleSheet("color: #b0b0b0;")
        layout.addWidget(self.status_label)
        
        layout.addSpacing(10)
        
        python_label = QLabel("Python")
        python_label.setStyleSheet("color: #e0e0e0; font-size: 11px;")
        layout.addWidget(python_label)
        self.python_progress = QProgressBar()
        self.python_progress.setMaximum(100)
        self.python_progress.setStyleSheet(self._get_progress_stylesheet())
        layout.addWidget(self.python_progress)
        
        node_label = QLabel("Node")
        node_label.setStyleSheet("color: #e0e0e0; font-size: 11px;")
        layout.addWidget(node_label)
        self.node_progress = QProgressBar()
        self.node_progress.setMaximum(100)
        self.node_progress.setStyleSheet(self._get_progress_stylesheet())
        layout.addWidget(self.node_progress)
        
        layout.addSpacing(5)
        
        # Update button (hidden by default)
        self.update_button = QPushButton("Update Available - Click to Install")
        self.update_button.setStyleSheet("""
            QPushButton {
                background-color: #00d4ff;
                color: #1a1a2e;
                border: none;
                padding: 10px;
                border-radius: 3px;
                font-weight: bold;
                font-size: 11px;
            }
            QPushButton:hover {
                background-color: #00e6ff;
            }
        """)
        self.update_button.clicked.connect(self.perform_update)
        self.update_button.hide()
        layout.addWidget(self.update_button)
        
        layout.addStretch()
        
        central_widget.setLayout(layout)
    
    def check_updates(self):
        """Check for updates in background thread."""
        self.update_thread = QThread()
        self.update_checker = UpdateChecker()
        self.update_checker.moveToThread(self.update_thread)
        
        self.update_thread.started.connect(self.update_checker.check)
        self.update_checker.update_found.connect(self.on_update_found)
        self.update_checker.check_complete.connect(self.start_installation)
        self.update_checker.check_complete.connect(self.update_thread.quit)
        
        self.update_thread.start()
    
    def on_update_found(self, result):
        """Handle update found."""
        self.update_info = result
        self.update_button.show()
    
    def perform_update(self):
        """Download and apply update, then close and restart."""
        if not self.update_info or self.installing_update:
            return
        
        reply = QMessageBox.question(
            self,
            "Update OwlSamba",
            f"Install version {self.update_info.get('remote_version')}?\n\n"
            f"Your .env and database will be preserved.\n"
            f"The app will close and reopen after updating.",
            QMessageBox.Yes | QMessageBox.No
        )
        
        if reply == QMessageBox.Yes:
            self.installing_update = True
            self.update_button.setEnabled(False)
            self.status_label.setText("Installing update...")
            
            def do_update():
                try:
                    from update.downloader import download_release, extract_and_apply_update
                    
                    version = self.update_info.get('remote_version')
                    success, path = download_release(version)
                    
                    if success:
                        success, msg = extract_and_apply_update(path)
                        
                        if success:
                            # Update VERSION file
                            version_file = ROOT / "VERSION"
                            version_file.write_text(version)
                            
                            # Close this window and restart
                            self.close()
                            
                            # Restart the launcher
                            subprocess.Popen([sys.executable, str(ROOT / "run.pyw")])
                            QApplication.quit()
                        else:
                            QMessageBox.critical(
                                self,
                                "Update Failed",
                                f"Error: {msg}"
                            )
                            self.installing_update = False
                            self.update_button.setEnabled(True)
                    else:
                        QMessageBox.critical(
                            self,
                            "Download Failed",
                            f"Error: {path}"
                        )
                        self.installing_update = False
                        self.update_button.setEnabled(True)
                
                except Exception as e:
                    QMessageBox.critical(
                        self,
                        "Update Error",
                        f"Error: {str(e)}"
                    )
                    self.installing_update = False
                    self.update_button.setEnabled(True)
            
            threading.Thread(target=do_update, daemon=True).start()
    
    def start_installation(self):
        """Start installation threads."""
        if self.installing_update:
            return
        
        self.status_label.setText("Installing dependencies")
        
        self.python_thread = QThread()
        self.python_worker = InstallWorker("python")
        self.python_worker.moveToThread(self.python_thread)
        
        self.python_thread.started.connect(self.python_worker.run)
        self.python_worker.python_progress.connect(self.python_progress.setValue)
        self.python_worker.finished.connect(self.on_python_finished)
        self.python_worker.finished.connect(self.python_thread.quit)
        
        self.node_thread = QThread()
        self.node_worker = InstallWorker("node")
        self.node_worker.moveToThread(self.node_thread)
        
        self.node_thread.started.connect(self.node_worker.run)
        self.node_worker.node_progress.connect(self.node_progress.setValue)
        self.node_worker.finished.connect(self.on_node_finished)
        self.node_worker.finished.connect(self.node_thread.quit)
        
        self.python_thread.start()
        self.node_thread.start()
    
    def on_python_finished(self, success):
        """Handle Python installation completion."""
        self.python_done = True
        self.python_success = success
        self.check_all_done()
    
    def on_node_finished(self, success):
        """Handle Node installation completion."""
        self.node_done = True
        self.node_success = success
        self.check_all_done()
    
    def check_all_done(self):
        """Check if all installations are complete."""
        if self.python_done and self.node_done:
            if self.python_success and self.node_success:
                self.status_label.setText("Ready to launch")
                threading.Timer(1.0, self.launch_service).start()
    
    def launch_service(self):
        """Launch the service."""
        if DEBUG_MODE:
            self.status_label.setText("DEBUG MODE: Service launch skipped")
            return
        
        service_pyw = BACKEND_DIR / "service.pyw"
        if not service_pyw.exists():
            return
        
        try:
            subprocess.Popen([sys.executable, str(service_pyw)])
            threading.Timer(2.0, self.close).start()
        except Exception:
            pass
    
    def _get_stylesheet(self):
        return """
        QMainWindow {
            background-color: #1a1a2e;
        }
        QProgressBar {
            border: none;
            border-radius: 3px;
            background-color: #2d2d44;
            height: 6px;
        }
        QProgressBar::chunk {
            background-color: #00d4ff;
            border-radius: 3px;
        }
        """
    
    def _get_progress_stylesheet(self):
        return """
        QProgressBar {
            border: none;
            border-radius: 3px;
            background-color: #2d2d44;
            height: 6px;
        }
        QProgressBar::chunk {
            background-color: #00d4ff;
            border-radius: 3px;
        }
        """


def main():
    app = QApplication(sys.argv)
    window = LauncherWindow()
    window.show()
    sys.exit(app.exec_())


if __name__ == "__main__":
    main()
