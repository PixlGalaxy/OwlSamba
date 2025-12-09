"""
OwlSamba Launcher with PyQt5 UI
Update checking and module installation management
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

ROOT = Path(__file__).resolve().parent
BACKEND_DIR = ROOT / "backend"
UPDATE_DIR = ROOT / "update"
MODULES_FLAG = ROOT / "modules_installed"

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


class LauncherWindow(QMainWindow):
    """Main launcher window."""
    
    def __init__(self):
        super().__init__()
        self.update_info = None
        self.installing_update = False
        self.init_ui()
        self.check_updates()
    
    def init_ui(self):
        """Initialize UI components."""
        self.setWindowTitle("OwlSamba")
        self.setGeometry(100, 100, 500, 250)
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
        
        self.status_label = QLabel("Initializing...")
        status_font = QFont()
        status_font.setPointSize(10)
        self.status_label.setFont(status_font)
        self.status_label.setStyleSheet("color: #b0b0b0;")
        layout.addWidget(self.status_label)
        
        layout.addSpacing(10)
        
        self.progress_bar = QProgressBar()
        self.progress_bar.setMaximum(100)
        self.progress_bar.setStyleSheet(self._get_progress_stylesheet())
        self.progress_bar.hide()
        layout.addWidget(self.progress_bar)
        
        layout.addSpacing(5)
        
        # Update button (hidden by default)
        self.update_button = QPushButton("Update Available - Click to Install")
        self.update_button.setStyleSheet(self._get_button_stylesheet())
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
        self.update_checker.check_complete.connect(self.on_check_complete)
        self.update_checker.check_complete.connect(self.update_thread.quit)
        
        self.update_thread.start()
    
    def on_update_found(self, result):
        """Handle update found."""
        self.update_info = result
        self.update_button.show()
    
    def on_check_complete(self):
        """Handle check complete - launch app."""
        if self.update_info:
            self.status_label.setText("Update available - Click button to install")
        else:
            self.status_label.setText("Ready to launch")
            threading.Timer(1.0, self.launch_app).start()
    
    def perform_update(self):
        """Download and apply update, then close and restart."""
        if not self.update_info or self.installing_update:
            return
        
        reply = QMessageBox.question(
            self,
            "Update OwlSamba",
            f"Install version {self.update_info.get('remote_version')}?\n\n"
            f"Your .env and database will be preserved.\n"
            f"The app will close and install the update.",
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
                            
                            # Close this window
                            self.close()
                            
                            # Wait 30 seconds then restart
                            time.sleep(30)
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
    
    def launch_app(self):
        """Launch the service application."""
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
    
    def _get_button_stylesheet(self):
        return """
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
        """


def main():
    app = QApplication(sys.argv)
    window = LauncherWindow()
    window.show()
    sys.exit(app.exec_())


if __name__ == "__main__":
    main()
