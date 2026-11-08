import unittest
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'tests'))

loader = unittest.TestLoader()
suite = loader.discover(str(ROOT / 'tests'), top_level_dir=str(ROOT / 'tests'))

runner = unittest.TextTestRunner(verbosity=2)
result = runner.run(suite)
print(f"\n==========================================")
print(f"Total tests ran: {result.testsRun}")
print(f"Failures: {len(result.failures)}")
print(f"Errors: {len(result.errors)}")
print(f"Successful: {result.wasSuccessful()}")
print(f"==========================================\n")
sys.exit(0 if result.wasSuccessful() else 1)
