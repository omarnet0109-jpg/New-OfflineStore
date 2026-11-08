"""Audit regressions for the real SQLite/WPF recovery and valuation paths."""
from pathlib import Path
from tempfile import TemporaryDirectory
from contextlib import closing
import sqlite3
import unittest

ROOT=Path(__file__).resolve().parents[1]
APP=ROOT/"src"/"OfflineStore"

class ProductionSafetyAudit(unittest.TestCase):
    def test_recovery_is_staged_not_in_place_while_ui_is_open(self):
        source=(APP/"Infrastructure"/"SqliteRecoveryService.cs").read_text(encoding="utf-8")
        self.assertIn("public void StageRestore(string sourcePath, bool replaceQueuedRequest = false)",source)
        self.assertIn("ApplyPendingRestoreAtStartup()",source)
        self.assertIn("StoreDb.VerifyIntegrity(connection)",source)
        self.assertIn("File.Replace(install,current,archiveThis,true)",source)
        self.assertIn('archiveThis + "-wal"',source)
        self.assertIn("DurableCopy(incoming,install)",source)
        self.assertIn("ClearAllPools()",source)
        self.assertIn("ReadOnly = true",source)

    def test_sensitive_restore_is_role_checked(self):
        service=(APP/"Services"/"StoreService.cs").read_text(encoding="utf-8")
        self.assertIn('public void StageDatabaseRestore(string backupFile)',service)
        fragment=service.split("public void StageDatabaseRestore(string backupFile)",1)[1].split("public void BackupTo",1)[0]
        self.assertIn('Require("BACKUP")',fragment)
        ui=(APP/"MainWindow.xaml").read_text(encoding="utf-8")
        self.assertIn('Click="RestoreBackupClick"',ui)
        self.assertIn('IsEnabled="{Binding CanBackup}"',ui)

    def test_sale_cost_comes_from_moving_average(self):
        service=(APP/"Services"/"StoreService.cs").read_text(encoding="utf-8")
        self.assertIn('"SELECT avg_cost_millicents FROM products WHERE id=@p0 AND active=1"',service)
        self.assertIn("unit_cost_snapshot_cents",service)
        qa=(APP/"Diagnostics"/"PreviewScreenshotCapture.cs").read_text(encoding="utf-8")
        self.assertIn("QA: sale snapshot uses last supplier cost instead of weighted average",qa)

    def test_opening_second_app_is_blocked_before_database_initialize(self):
        app=(APP/"App.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("new Mutex(true",app)
        self.assertLess(app.index("new Mutex(true"),app.index("service.Initialize()"))
        self.assertIn("ReleaseMutex()",app)

    def test_f2_is_context_aware(self):
        ui=(APP/"MainWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("e.Key == Key.F2 && GoodsReceiptTab.IsSelected",ui)
        self.assertIn("GoodsReceiptPanel.FocusProductSearch()",ui)
        self.assertIn("SaleBarcodeInput.Focus()",ui)

    def test_sqlite_vacuum_snapshot_real_round_trip(self):
        # Independently prove the chosen SQLite primitive produces a consistent
        # backup even when its source database uses WAL.
        with TemporaryDirectory() as tmp:
            src=str(Path(tmp)/"store.db")
            snap=str(Path(tmp)/"backup.db")
            with closing(sqlite3.connect(src)) as db:
                db.execute("PRAGMA journal_mode=WAL")
                db.execute("CREATE TABLE sample(id INTEGER PRIMARY KEY, amount INTEGER NOT NULL)")
                db.execute("INSERT INTO sample VALUES(1,12345)")
                db.commit()
                db.execute("VACUUM INTO ?",(snap,))
            with closing(sqlite3.connect(snap)) as restored:
                self.assertEqual("ok",restored.execute("PRAGMA integrity_check").fetchone()[0])
                self.assertEqual(12345,restored.execute("SELECT amount FROM sample WHERE id=1").fetchone()[0])

if __name__=="__main__":
    unittest.main()
