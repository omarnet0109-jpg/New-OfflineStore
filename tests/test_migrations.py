"""Tests for the actual v1 -> v2 database migration SQL; not a WPF runtime test."""
import pathlib
import sqlite3
import tempfile
import unittest
from contextlib import closing

ROOT = pathlib.Path(__file__).resolve().parents[1]
BASE = (ROOT/'src/OfflineStore/Infrastructure/Schema.sql').read_text(encoding='utf-8')
MIGRATION = (ROOT/'src/OfflineStore/Infrastructure/Migrations/002_settings_and_security.sql').read_text(encoding='utf-8')


class MigrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.path = pathlib.Path(self.temp.name)/'real.sqlite'
        self.db = sqlite3.connect(self.path)
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.executescript(BASE)
        self.db.execute('PRAGMA user_version=1')
        self.db.execute("INSERT INTO products(sku,name,unit,sale_price_cents,created_utc) VALUES('X1','قلم','قطعة',550,'2026-01-01')")
        self.db.commit()

    def tearDown(self):
        self.db.close()
        self.temp.cleanup()

    def test_settings_default_and_existing_data_preserved(self):
        self.db.execute('BEGIN')
        # executescript itself commits; SQL file's schema tested as executed in production.
        self.db.executescript(MIGRATION)
        self.db.execute('PRAGMA user_version=2')
        self.assertEqual(self.db.execute('PRAGMA user_version').fetchone()[0], 2)
        self.assertEqual(dict(self.db.execute('SELECT setting_key,setting_value FROM app_settings')),
                         {'theme':'light','accent':'blue'})
        self.assertEqual(self.db.execute('SELECT name FROM products WHERE sku="X1"').fetchone()[0], 'قلم')
        self.assertEqual(self.db.execute('SELECT COUNT(*) FROM local_auth').fetchone()[0], 0)

    def test_password_table_rejects_plain_text_and_weak_iterations(self):
        self.db.executescript(MIGRATION)
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO local_auth(id,salt,password_hash,iterations,updated_utc) VALUES(1,?,?,500,'now')",
                            (b'a'*32, b'b'*32))
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO local_auth(id,salt,password_hash,iterations,updated_utc) VALUES(1,?,?,210000,'now')",
                            (b'plaintext', b'b'*32))

    def test_settings_reject_unknown_keys(self):
        self.db.executescript(MIGRATION)
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("INSERT INTO app_settings(setting_key,setting_value) VALUES('password','oops')")

    def test_backward_compatible_backup_of_version_one(self):
        dest = pathlib.Path(self.temp.name)/'backup-before-migration.db'
        self.db.execute('VACUUM INTO ?', (str(dest),))
        self.db.executescript(MIGRATION)
        with closing(sqlite3.connect(dest)) as backup:
            self.assertEqual(backup.execute('PRAGMA user_version').fetchone()[0], 1)
            self.assertEqual(backup.execute('SELECT name FROM products').fetchone()[0], 'قلم')
            self.assertEqual(backup.execute('PRAGMA integrity_check').fetchone()[0], 'ok')

if __name__ == '__main__':
    unittest.main()
