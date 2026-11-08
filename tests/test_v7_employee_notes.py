"""Employee notes storage tests on the same real SQLite migration used in production."""
import pathlib
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
INFRA = ROOT / "src" / "OfflineStore" / "Infrastructure"


class EmployeeNotesTests(unittest.TestCase):
    def setUp(self):
        self.c = sqlite3.connect(":memory:")
        self.c.execute("PRAGMA foreign_keys=ON")
        self.c.executescript((INFRA / "Schema.sql").read_text(encoding="utf-8"))
        for path in sorted((INFRA / "Migrations").glob("*.sql")):
            self.c.executescript(path.read_text(encoding="utf-8"))
        self.c.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
             VALUES('worker','موظف','CASHIER',zeroblob(32),zeroblob(32),210000,'now')""")
        self.c.commit()

    def tearDown(self):
        self.c.close()

    def insert(self, title="ملحوظة", rtf=r"{\rtf1\ansi Note\par}"):
        return self.c.execute("""INSERT INTO employee_notes
            (title,body_rtf,created_by_user_id,created_utc,updated_utc)
            VALUES(?,?,1,'now','now')""", (title, rtf)).lastrowid

    def test_arabic_note_and_rtf_survive_round_trip(self):
        note_id = self.insert("يوم الجرد", r"{\rtf1\ansi\b Stock\b0\par}")
        row = self.c.execute("SELECT title,body_rtf FROM employee_notes WHERE id=?", (note_id,)).fetchone()
        self.assertEqual("يوم الجرد", row[0])
        self.assertIn("\\b Stock", row[1])

    def test_empty_title_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.insert(title="   ")

    def test_long_title_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.insert(title="x" * 121)

    def test_oversized_document_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.insert(rtf="x" * 200001)

    def test_orphan_author_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.c.execute("""INSERT INTO employee_notes
                (title,body_rtf,created_by_user_id,created_utc,updated_utc)
                VALUES('hello','text',999,'now','now')""")

    def test_notes_survive_reconnection(self):
        self.insert()
        self.c.commit()  # SQLite online backup requires the write transaction to finish.
        backup = sqlite3.connect(":memory:")
        self.c.backup(backup)
        self.assertEqual(1, backup.execute("SELECT COUNT(*) FROM employee_notes").fetchone()[0])
        self.assertEqual("ok", backup.execute("PRAGMA integrity_check").fetchone()[0])
        backup.close()

    def test_service_enforces_edit_ownership(self):
        service = (ROOT/"src/OfflineStore/Services/EmployeeNotesService.cs").read_text(encoding="utf-8")
        self.assertIn("created_by_user_id=@actor OR @admin=1", service)
        self.assertIn('Authorize(_session, "VIEW")', service)
        self.assertIn("NOTE_UPDATED", service)

    def test_wpf_has_optional_suggestions(self):
        xaml = (ROOT/"src/OfflineStore/MainWindow.xaml").read_text(encoding="utf-8")
        csharp = (ROOT/"src/OfflineStore/MainWindow.Enhancements.cs").read_text(encoding="utf-8")
        self.assertIn('Header="ملحوظات"', xaml)
        self.assertIn('ProductNameKeyDown', xaml)
        self.assertIn('e.Key == Key.Enter', csharp)
        self.assertIn('ProductSuggestionPopup.IsOpen = false', csharp)
        self.assertIn('Take(6)', csharp)

    def test_xaml_well_formed(self):
        from xml.etree import ElementTree
        for p in (ROOT/"src/OfflineStore").rglob("*.xaml"):
            with self.subTest(path=p.name):
                ElementTree.parse(p)


if __name__ == "__main__":
    unittest.main()
