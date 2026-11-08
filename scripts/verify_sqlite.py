"""Pin SHA3-256 from the official SQLite downloads page. Prevent unreviewed DLL updates."""
import hashlib
import pathlib
import sys

EXPECTED_SHA3_256 = '97cd8f207ec121e13d3af459e82834a214700659f307603d80814c6bdb19e2d2'
if len(sys.argv) != 2:
    sys.exit('Usage: python scripts/verify_sqlite.py sqlite-win32.zip')
actual = hashlib.sha3_256(pathlib.Path(sys.argv[1]).read_bytes()).hexdigest()
if actual != EXPECTED_SHA3_256:
    sys.exit(f'SQLite archive hash mismatch: {actual}')
print('Official SQLite x86 archive SHA3-256 verified')
