from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1]
class UpgradeBackupRecoveryTests(unittest.TestCase):
 def test_unavailable_external_backup_has_a_distinct_recoverable_error(self):
  s=(ROOT/'src/OfflineStore/Infrastructure/StoreDb.cs').read_text(encoding='utf-8')
  self.assertIn('throw new UpgradeBackupRequiredException',s)
  body=s.split('public void Initialize()',1)[1]
  self.assertLess(body.index('EnsureUpgradeBackupReady'),body.index('ApplyScript('))
 def test_startup_reconnect_or_reconfigure_requires_admin(self):
  s=(ROOT/'src/OfflineStore/App.xaml.cs').read_text(encoding='utf-8')
  self.assertIn('catch (UpgradeBackupRequiredException',s)
  self.assertIn('ConfigureExternalBackupFolder',s)
  self.assertIn('service.Can("USERS")',s)
