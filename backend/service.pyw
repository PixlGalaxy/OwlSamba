"""
OwlSamba Windows Service (No Console Window)
Entry point for running as Windows Service with system tray icon.
"""

import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent))

from service import main

if __name__ == "__main__":
    main()
