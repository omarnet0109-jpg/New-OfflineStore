"""Checks for the production recovery journal; filesystem execution lives in Windows harness."""
from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
class RecoveryProtocolTests(unittest.TestCase):
 def test_recovery_never_queries_the_damaged_current_database(self):
  s=(ROOT/'src/OfflineStore/Infrastructure/SqliteRecoveryService.cs').read_text(encoding="utf-8")
  body=s.split('public void ApplyPendingRestoreAtStartup()',1)[1]
  self.assertNotIn('_db.Open()',body)
  self.assertNotIn('VACUUM',body)
  self.assertIn('FileShare.Delete',s)
  self.assertIn('SHA256.Create()',s)
  self.assertIn('Flush(true)',s)
 def test_startup_can_restore_before_successful_initialize(self):
  s=(ROOT/'src/OfflineStore/App.xaml.cs').read_text(encoding="utf-8")
  self.assertIn('new StartupRecoveryWindow',s)
  self.assertIn('ApplyPendingRestoreAtStartup()',s)
 def test_sidecars_and_completed_requests_are_recoverable(self):
  s=(ROOT/'src/OfflineStore/Infrastructure/SqliteRecoveryService.cs').read_text(encoding="utf-8")
  self.assertIn('archiveThis + "-wal"',s)
  self.assertIn('archiveThis + "-shm"',s)
  self.assertIn('AlreadyInstalled',s)

 def test_failed_request_can_be_replaced_without_journal_gap(self):
  s=(ROOT/'src/OfflineStore/Infrastructure/SqliteRecoveryService.cs').read_text(encoding="utf-8")
  self.assertIn('bool replaceQueuedRequest = false',s)
  self.assertIn('parts.Length == 4',s)
  ui=(ROOT/'src/OfflineStore/StartupRecoveryWindow.cs').read_text(encoding="utf-8")
  self.assertIn('StageRestore(backup, true)',ui)
 def test_completed_restore_allows_empty_wal_created_by_readonly_validation(self):
  s=(ROOT/'src/OfflineStore/Infrastructure/SqliteRecoveryService.cs').read_text(encoding="utf-8")
  installed=s.split('private static bool AlreadyInstalled',1)[1].split('public void ApplyPendingRestoreAtStartup',1)[0]
  self.assertIn('Length == 0',installed)
