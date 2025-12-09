"""
Update downloader for OwlSamba
Downloads and extracts updates from GitHub releases
Only accessible from run.pyw for security
"""

import os
import shutil
import zipfile
import requests
from pathlib import Path

GITHUB_REPO = "PixlGalaxy/OwlSamba"
ROOT_DIR = Path(__file__).parent.parent

# Security: Only allow run.pyw to call this module
_ALLOWED_CALLER = "run.pyw"

def _verify_caller():
    """Verify that this is being called from run.pyw"""
    import inspect
    stack = inspect.stack()
    for frame_info in stack:
        if _ALLOWED_CALLER in frame_info.filename:
            return True
    return False

# Files/folders to skip during update (data protection)
SKIP_PATTERNS = [
    '.env',
    '.env.example',
    'data/',
    'logs/',
    'backend/database.db',
    'backend/sqlite.db',
    '.git/',
    '.gitignore',
    '__pycache__',
    'node_modules/',
    '.vscode/',
    'VERSION',
    'update/',
]


def download_release(version: str, progress_callback=None) -> tuple[bool, str]:
    """
    Download release from GitHub
    Returns: (success: bool, file_path: str)
    """
    if not _verify_caller():
        return False, "Unauthorized access"
    try:
        url = f"https://github.com/{GITHUB_REPO}/archive/refs/tags/v{version}.zip"
        file_path = ROOT_DIR / "update" / f"owlsamba-{version}.zip"
        
        response = requests.get(url, stream=True, timeout=30)
        response.raise_for_status()
        
        total_size = int(response.headers.get('content-length', 0))
        downloaded = 0
        
        with open(file_path, 'wb') as f:
            for chunk in response.iter_content(chunk_size=8192):
                if chunk:
                    f.write(chunk)
                    downloaded += len(chunk)
                    if progress_callback and total_size:
                        progress_callback(int(downloaded / total_size * 100))
        
        return True, str(file_path)
    
    except Exception as e:
        return False, str(e)


def should_skip(path: str) -> bool:
    """Check if path should be skipped during update"""
    path = path.replace('\\', '/').lower()
    
    for pattern in SKIP_PATTERNS:
        pattern = pattern.replace('\\', '/').lower()
        if path == pattern or path.startswith(pattern.rstrip('/')):
            return True
    
    return False


def extract_and_apply_update(zip_file: str, progress_callback=None) -> tuple[bool, str]:
    """
    Extract update zip and copy files to root (skipping protected files)
    Returns: (success: bool, message: str)
    """
    if not _verify_caller():
        return False, "Unauthorized access"
    try:
        zip_path = Path(zip_file)
        extract_dir = ROOT_DIR / "update" / "temp"
        
        if extract_dir.exists():
            shutil.rmtree(extract_dir)
        
        extract_dir.mkdir(parents=True, exist_ok=True)
        
        with zipfile.ZipFile(zip_path, 'r') as zip_ref:
            zip_ref.extractall(extract_dir)
        
        # Find the extracted folder (GitHub creates OwlSamba-v1.0.0 format)
        extracted_folders = list(extract_dir.iterdir())
        if not extracted_folders:
            return False, "Empty zip file"
        
        source_dir = extracted_folders[0]
        
        # Copy files, skipping protected ones
        total_files = sum(1 for _ in source_dir.rglob('*'))
        copied = 0
        
        for source_file in source_dir.rglob('*'):
            relative_path = source_file.relative_to(source_dir)
            
            if should_skip(str(relative_path)):
                continue
            
            dest_file = ROOT_DIR / relative_path
            
            if source_file.is_file():
                dest_file.parent.mkdir(parents=True, exist_ok=True)
                shutil.copy2(source_file, dest_file)
            elif source_file.is_dir():
                dest_file.mkdir(parents=True, exist_ok=True)
            
            copied += 1
            if progress_callback and total_files:
                progress_callback(int(copied / total_files * 100))
        
        # Cleanup
        shutil.rmtree(extract_dir)
        zip_path.unlink()
        
        return True, "Update applied successfully"
    
    except Exception as e:
        return False, f"Error applying update: {str(e)}"


def cleanup_old_downloads():
    """Remove old download zips"""
    update_dir = ROOT_DIR / "update"
    if update_dir.exists():
        for file in update_dir.glob("owlsamba-*.zip"):
            try:
                file.unlink()
            except:
                pass


if __name__ == "__main__":
    import sys
    
    if len(sys.argv) > 1:
        version = sys.argv[1]
        
        print(f"Downloading version {version}...")
        success, result = download_release(version)
        
        if success:
            print(f"Downloaded to {result}")
            print("Extracting and applying update...")
            success, msg = extract_and_apply_update(result)
            print(msg)
        else:
            print(f"Error: {result}")
