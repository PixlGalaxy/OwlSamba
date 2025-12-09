"""
Script de prueba para el sistema de actualización
"""

import sys
import json
from pathlib import Path

ROOT = Path(__file__).parent.parent
sys.path.insert(0, str(ROOT / "update"))

from checker import check_for_updates, get_local_version, get_remote_version

print("=" * 60)
print("OwlSamba Update System - Test")
print("=" * 60)
print()

print("1. Versión Local:")
local = get_local_version()
print(f"   {local}")
print()

print("2. Verificando versión remota...")
success, remote = get_remote_version()
if success:
    print(f"   {remote}")
else:
    print("   Error conectando a GitHub")
print()

print("3. Verificando updates disponibles...")
result = check_for_updates()
print(json.dumps(result, indent=2, ensure_ascii=False))
print()

if result.get('has_update'):
    print("✓ Actualización disponible!")
    print(f"  Local: {result['local_version']} → Remoto: {result['remote_version']}")
else:
    print("✓ Ya está en la versión más reciente")
