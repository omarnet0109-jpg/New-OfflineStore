"""Offline UI and external backup safety regressions (no network required)."""
from pathlib import Path
from xml.etree import ElementTree
import unittest

ROOT = Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "OfflineStore"


class OfflineDeliveryRegressions(unittest.TestCase):
    def test_external_backup_is_not_user_selectable_on_windows_drive(self):
        service = (APP / "Infrastructure" / "ExternalBackupService.cs").read_text(
            encoding="utf-8"
        )
        self.assertIn("Path.GetPathRoot(Environment.SystemDirectory)", service)
        self.assertIn("string.Equals(root, systemRoot", service)
        self.assertIn("DriveType.Removable", service)
        self.assertIn("DriveType.Fixed", service)
        self.assertIn('full.StartsWith(@"\\\\", StringComparison.Ordinal)', service)
        self.assertIn("VerifyBackupFile(temp)", service)
        self.assertIn("File.Move(temp, final)", service)
        self.assertIn("finally", service)
        self.assertIn("File.Delete(temp)", service)
        self.assertIn("TimeSpan.FromMinutes(20)", service)
        self.assertIn("OfflineStore-Auto-*.db", service)
        self.assertIn('VolumeMarker = ".offlinestore-volume-id"', service)
        self.assertIn('File.ReadAllLines(ConfigFile, Encoding.UTF8)', service)
        self.assertIn('Guid.NewGuid().ToString("N")', service)
        self.assertIn('تم تغيير وسيط النسخ أو حرف القرص', service)

    def test_manual_ui_uses_only_external_backup_service(self):
        vm = (APP / "ViewModels" / "MainViewModel.cs").read_text(encoding="utf-8")
        section = vm.split("BackupCommand = new RelayCommand", 1)[1].split(
            "var current = _service.Appearance.Load()", 1
        )[0]
        self.assertIn("_service.CreateExternalBackup()", section)
        self.assertNotIn("_service.BackupTo(dialog.FileName)", section)
        service = (APP / "Services" / "StoreService.cs").read_text(encoding="utf-8")
        part = service.split("public string ConfigureExternalBackupFolder", 1)[1]
        self.assertIn('Require("BACKUP")', part)
        self.assertIn('public string CreateScheduledExternalBackupIfDue()', part)
        self.assertIn("new ExternalBackupService(_db)", part)

    def test_notes_are_existing_data_not_new_duplicate_storage(self):
        window = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        ElementTree.fromstring(window)
        self.assertEqual(window.count('x:Name="NotesTab"'), 1)
        self.assertIn('x:Name="NotesEditor"', window)
        self.assertIn('x:Name="NotesSearchBox"', window)
        self.assertIn('Click="GoNotesClick"', window)
        self.assertIn('Click="ConfigureBackupClick"', window)
        self.assertIn('x:Name="ExternalBackupPathText"', window)
        self.assertIn('x:Name="BackupStatusText"', window)
        code = (APP / "MainWindow.Enhancements.cs").read_text(encoding="utf-8")
        self.assertIn("NotesSearchTextChanged(", code)
        self.assertIn("ConfirmNoteChanges()", code)
        self.assertIn("NotesSearchBox.Clear()", code)
        self.assertIn("_service.Notes.ListAll()", code)
        self.assertIn("_service.Notes.Update(", code)
        self.assertIn("_service.Notes.Create(", code)

    def test_scheduled_backup_is_off_ui_thread_and_offline(self):
        code = (APP / "MainWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("Task.Run(() => _service.CreateScheduledExternalBackupIfDue())", code)
        self.assertIn("DispatcherTimer", code)
        self.assertIn("Interlocked.CompareExchange", code)
        self.assertIn("Key.F2", code)
        self.assertIn("Key.F5", code)
        self.assertIn("Key.S", code)
        self.assertIn("Key.N", code)
        for path in (APP / "Infrastructure").glob("*.cs"):
            contents = path.read_text(encoding="utf-8")
            self.assertNotIn("HttpClient", contents)
            self.assertNotIn("WebRequest", contents)

    def test_existing_sales_purchase_supply_and_backup_still_present(self):
        s = (APP / "Services" / "StoreService.cs").read_text(encoding="utf-8")
        self.assertIn("public string Post(bool purchase", s)
        self.assertIn("public void BackupTo(string absolutePath)", s)
        self.assertIn("public void StageDatabaseRestore", s)
        self.assertIn("ApplyMovingAverageCost", s)
        x = (APP / "MainWindow.xaml").read_text(encoding="utf-8")
        self.assertIn('x:Name="GoodsReceiptTab"', x)
        self.assertIn('x:Name="SalesTab"', x)
        self.assertIn('x:Name="ProductsTab"', x)


if __name__ == "__main__":
    unittest.main()
