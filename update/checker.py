"""
Version checker for OwlSamba
Checks GitHub releases for new versions
Only accessible from run.pyw for security
"""

import json
import requests
from pathlib import Path
from packaging import version

GITHUB_REPO = "PixlGalaxy/OwlSamba"
GITHUB_API = f"https://api.github.com/repos/{GITHUB_REPO}/releases/latest"

# Version stored locally
VERSION_FILE = Path(__file__).parent.parent / "VERSION"

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


def get_local_version() -> str:
    """Get local version from VERSION file or default to 0.0.0"""
    if VERSION_FILE.exists():
        return VERSION_FILE.read_text().strip()
    return "0.0.0"


def get_remote_version() -> tuple[bool, str]:
    """
    Get latest version from GitHub releases
    Returns: (success: bool, version: str)
    """
    try:
        response = requests.get(GITHUB_API, timeout=5)
        response.raise_for_status()
        
        data = response.json()
        tag = data.get("tag_name", "").lstrip("v")
        
        if tag:
            return True, tag
        return False, ""
    
    except requests.RequestException as e:
        return False, ""


def check_for_updates() -> dict:
    """
    Check if there's a new version available
    Returns: {
        'has_update': bool,
        'local_version': str,
        'remote_version': str,
        'download_url': str,
        'release_notes': str
    }
    """
    if not _verify_caller():
        return {'error': 'Unauthorized access', 'has_update': False}
    local = get_local_version()
    success, remote = get_remote_version()
    
    result = {
        'has_update': False,
        'local_version': local,
        'remote_version': remote,
        'download_url': '',
        'release_notes': '',
        'error': None
    }
    
    if not success:
        result['error'] = 'Could not reach GitHub API'
        return result
    
    try:
        local_ver = version.parse(local)
        remote_ver = version.parse(remote)
        
        if remote_ver > local_ver:
            result['has_update'] = True
            result['remote_version'] = remote
            
            response = requests.get(GITHUB_API, timeout=5)
            data = response.json()
            result['download_url'] = f"https://github.com/{GITHUB_REPO}/releases/tag/{data.get('tag_name', '')}"
            result['release_notes'] = data.get('body', '')
    
    except Exception as e:
        result['error'] = str(e)
    
    return result


if __name__ == "__main__":
    result = check_for_updates()
    print(json.dumps(result, indent=2))
