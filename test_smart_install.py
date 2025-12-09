#!/usr/bin/env python3
"""
Test script for smart dependency detection and installation logic.
Tests the functions that will be used in run.pyw.
"""

from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parent
BACKEND_DIR = ROOT / "backend"

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

def parse_requirements(requirements_file):
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
    except Exception as e:
        print(f"Error parsing requirements: {e}")
        return []

def check_missing_packages(packages):
    """Check which packages from list are not installed."""
    missing = []
    for package in packages:
        # Get the import name from mapping, or derive it
        import_name = PACKAGE_TO_IMPORT.get(package, package.replace('-', '_').lower())
        try:
            __import__(import_name)
            print(f"✓ {package:<20} - installed")
        except ImportError:
            print(f"✗ {package:<20} - MISSING")
            missing.append(package)
    return missing

if __name__ == "__main__":
    print("=" * 60)
    print("Testing Smart Dependency Detection")
    print("=" * 60)
    print()
    
    requirements_file = BACKEND_DIR / "requirements.txt"
    
    print(f"Reading {requirements_file}")
    packages = parse_requirements(requirements_file)
    
    print(f"\nFound {len(packages)} packages in requirements.txt:")
    for pkg in packages:
        print(f"  - {pkg}")
    
    print("\n" + "=" * 60)
    print("Checking Installation Status")
    print("=" * 60)
    
    missing = check_missing_packages(packages)
    
    print("\n" + "=" * 60)
    print("Summary")
    print("=" * 60)
    
    if missing:
        print(f"\n✗ {len(missing)} package(s) missing:")
        for pkg in missing:
            print(f"  - pip install {pkg}")
        
        progress_step = 80 / len(missing)
        print(f"\nWith {len(missing)} packages to install:")
        print(f"  Progress step per package: {progress_step:.2f}%")
        print(f"  Expected progress: 10% → ", end="")
        current = 10
        for i in range(len(missing)):
            current += progress_step
            if i < len(missing) - 1:
                print(f"{int(current)}% → ", end="")
            else:
                print(f"{int(current)}% → 100%")
    else:
        print("\n✓ All packages are installed!")
        print("  Progress: 10% → 100%")
