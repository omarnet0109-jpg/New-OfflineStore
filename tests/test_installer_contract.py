import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]

class InstallerContracts(unittest.TestCase):
    def test_installer_preserves_user_database(self):
        raw = (ROOT / 'installer' / 'OfflineStore.nsi').read_text(encoding='utf-8-sig')
        self.assertIn('RequestExecutionLevel user', raw)
        self.assertIn('$LOCALAPPDATA\\Programs\\OfflineStore', raw)
        self.assertIn('RMDir /r "$INSTDIR"', raw)
        self.assertNotIn('RMDir /r "$LOCALAPPDATA\\OfflineStore"', raw)
        self.assertNotIn('Delete "$LOCALAPPDATA\\OfflineStore\\store.db"', raw)

    def test_net48_detection(self):
        raw = (ROOT / 'installer' / 'OfflineStore.nsi').read_text(encoding='utf-8-sig')
        self.assertIn('ReadRegDWORD', raw)
        self.assertIn('528040', raw)
        self.assertIn('Abort', raw)

    def test_windows_build_pipeline_builds_first(self):
        yaml = (ROOT / '.github' / 'workflows' / 'windows-build.yml').read_text()
        self.assertLess(yaml.index('Build WPF desktop app'), yaml.index('Build offline application Setup.exe'))
        self.assertIn('Upload Setup.exe preview', yaml)
        self.assertIn('Get-FileHash -Algorithm SHA256', yaml)

if __name__ == '__main__':
    unittest.main()
