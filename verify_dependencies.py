"""
Verify all required dependencies are listed in requirements.txt
"""

import re
import sys
from pathlib import Path

BACKEND_DIR = Path(__file__).parent / "backend"
REQUIREMENTS_FILE = BACKEND_DIR / "requirements.txt"

# All imports that should be in requirements.txt
REQUIRED_IMPORTS = {
    "fastapi": "fastapi",
    "uvicorn": "uvicorn",
    "dotenv": "python-dotenv",
    "pyqt5": "pyqt5",
    "bcrypt": "bcrypt",
    "slowapi": "slowapi",
    "tendo": "tendo",
    "PIL": "Pillow",
    "requests": "requests",
    "packaging": "packaging",
}

def get_requirements():
    """Parse requirements.txt and return list of packages"""
    if not REQUIREMENTS_FILE.exists():
        print(f"ERROR: {REQUIREMENTS_FILE} not found")
        return set()
    
    packages = set()
    with open(REQUIREMENTS_FILE, 'r') as f:
        for line in f:
            line = line.strip()
            if line and not line.startswith('#'):
                # Extract package name (before any version specifiers)
                pkg = re.split(r'[<>=!]', line)[0].strip().lower()
                packages.add(pkg)
    
    return packages

def check_imports_in_backend():
    """Check if all imports are in requirements.txt"""
    requirements = get_requirements()
    
    print("=" * 60)
    print("Checking Required Dependencies")
    print("=" * 60)
    
    all_ok = True
    
    for import_name, package_name in REQUIRED_IMPORTS.items():
        package_lower = package_name.lower()
        
        if package_lower in requirements:
            print(f"✓ {package_name:20} → {import_name}")
        else:
            print(f"✗ {package_name:20} → {import_name} (MISSING FROM requirements.txt)")
            all_ok = False
    
    print()
    print("=" * 60)
    
    if all_ok:
        print("✓ All dependencies are listed in requirements.txt")
        return 0
    else:
        print("✗ Some dependencies are missing from requirements.txt")
        print()
        print("Add the missing packages to:")
        print(f"  {REQUIREMENTS_FILE}")
        return 1

def verify_imports():
    """Try to import all required packages"""
    print("Verifying Imports...")
    print("=" * 60)
    
    missing = []
    
    for import_name, package_name in REQUIRED_IMPORTS.items():
        try:
            __import__(import_name)
            print(f"✓ import {import_name:20} OK")
        except ImportError:
            print(f"✗ import {import_name:20} FAILED (package not installed)")
            missing.append(package_name)
    
    print()
    print("=" * 60)
    
    if missing:
        print(f"✗ {len(missing)} package(s) not installed:")
        for pkg in missing:
            print(f"    pip install {pkg}")
        return 1
    else:
        print("✓ All imports successful")
        return 0

if __name__ == "__main__":
    print()
    check_result = check_imports_in_backend()
    print()
    import_result = verify_imports()
    print()
    
    sys.exit(max(check_result, import_result))
