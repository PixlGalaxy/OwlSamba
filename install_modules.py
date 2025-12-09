#!/usr/bin/env python3
"""
Module installer script for OwlSamba
Installs Python dependencies from requirements.txt files in backend and update directories
"""

import sys
import subprocess
from pathlib import Path

ROOT = Path(__file__).resolve().parent
BACKEND_DIR = ROOT / "backend"
UPDATE_DIR = ROOT / "update"

def parse_requirements(requirements_file):
    """Parse requirements.txt and return list of package names."""
    packages = []
    try:
        with open(requirements_file, 'r') as f:
            for line in f:
                line = line.strip()
                if line and not line.startswith('#'):
                    # Extract package name (before version specifiers)
                    pkg = line.split('==')[0].split('>=')[0].split('<=')[0].split('>')[0].split('<')[0].split('!=')[0].strip()
                    if pkg:
                        packages.append(pkg)
    except Exception as e:
        print(f"Error reading {requirements_file}: {e}")
    return packages

def check_installed(package):
    """Check if a package is installed."""
    # Map package names to import names
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
    
    import_name = PACKAGE_TO_IMPORT.get(package, package.replace('-', '_').lower())
    try:
        __import__(import_name)
        return True
    except ImportError:
        return False

def install_modules():
    """Install all required modules."""
    print("=" * 60)
    print("OwlSamba Module Installer")
    print("=" * 60)
    print()
    
    all_packages = []
    
    # Collect packages from backend
    backend_req = BACKEND_DIR / "requirements.txt"
    if backend_req.exists():
        print(f"Reading {backend_req}")
        packages = parse_requirements(backend_req)
        all_packages.extend(packages)
        print(f"  Found {len(packages)} packages")
    
    # Collect packages from update
    update_req = UPDATE_DIR / "requirements.txt"
    if update_req.exists():
        print(f"Reading {update_req}")
        packages = parse_requirements(update_req)
        all_packages.extend(packages)
        print(f"  Found {len(packages)} packages")
    
    print()
    
    # Remove duplicates while preserving order
    seen = set()
    unique_packages = []
    for pkg in all_packages:
        if pkg.lower() not in seen:
            seen.add(pkg.lower())
            unique_packages.append(pkg)
    
    print(f"Total unique packages: {len(unique_packages)}")
    print()
    
    # Check which are missing
    missing = []
    for pkg in unique_packages:
        if not check_installed(pkg):
            missing.append(pkg)
    
    if not missing:
        print("✓ All packages are already installed!")
        return True
    
    print(f"Missing {len(missing)} package(s):")
    for pkg in missing:
        print(f"  - {pkg}")
    print()
    
    # Install missing packages
    print("Installing packages...")
    print("-" * 60)
    
    for i, package in enumerate(missing, 1):
        print(f"[{i}/{len(missing)}] Installing {package}...", end=" ", flush=True)
        try:
            result = subprocess.run(
                [sys.executable, "-m", "pip", "install", package],
                capture_output=True,
                text=True,
                timeout=120
            )
            
            if result.returncode == 0:
                print("✓")
            else:
                print(f"✗ (Failed)")
                print(f"  Error: {result.stderr[:200]}")
                return False
        
        except subprocess.TimeoutExpired:
            print("✗ (Timeout)")
            return False
        except Exception as e:
            print(f"✗ (Error: {str(e)})")
            return False
    
    print()
    print("=" * 60)
    print("✓ All modules installed successfully!")
    print("=" * 60)
    return True

if __name__ == "__main__":
    success = install_modules()
    sys.exit(0 if success else 1)
