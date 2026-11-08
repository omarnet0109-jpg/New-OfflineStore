"""SQLite upgrade checks for COM device settings (not hardware or WPF tests)."""
import pathlib
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1] / 'src' / 'OfflineStore' / 'Infrastructure'
SQL1 = (ROOT / 'Schema.sql').read_text(encoding='utf-8')
SQL2 = (ROOT / 'Migrations' / '002_settings_and_security.sql').read_text(encoding='utf-8')
SQL3 = (ROOT / 'Migrations' / '003_units_users_alerts.sql').read_text(encoding='utf-8')
SQL4 = (ROOT / 'Migrations' / '004_serial_scanner.sql').read_text(encoding='utf-8')


class ScannerMigrationTests(unittest.TestCase):
    def setUp(self):
        self.c = sqlite3.connect(':memory:')
        self.c.execute('PRAGMA foreign_keys=ON')
        for script in (SQL1, SQL2, SQL3):
            self.c.executescript(script)
        self.c.execute("INSERT INTO products(sku,name,unit,sale_price_cents,created_utc) VALUES('SKU1','قلم','قطعة',500,'date')")
        self.c.execute("INSERT INTO app_settings(setting_key,setting_value) VALUES('accent','blue') ON CONFLICT(setting_key) DO NOTHING")
        self.c.executescript(SQL4)
        self.c.execute('PRAGMA user_version=4')

    def tearDown(self):
        self.c.close()

    def test_disabled_and_secure_defaults(self):
        self.assertEqual(self.c.execute('SELECT enabled,port_name,baud_rate,data_bits,parity,stop_bits,handshake FROM serial_scanner_config').fetchone(),
                         (0,'',9600,8,'None','One','None'))

    def test_configuration_does_not_modify_sales_or_stock(self):
        before = self.c.execute('SELECT COUNT(*) FROM products').fetchone()[0]
        self.c.execute("UPDATE serial_scanner_config SET enabled=1,port_name='COM5',baud_rate=19200 WHERE id=1")
        self.assertEqual(self.c.execute('SELECT COUNT(*) FROM products').fetchone()[0], before)
        self.assertEqual(self.c.execute('SELECT COUNT(*) FROM stock_movements').fetchone()[0], 0)
        self.assertEqual(self.c.execute('SELECT COUNT(*) FROM documents').fetchone()[0], 0)

    def test_invalid_settings_rejected(self):
        for column, value in [('enabled',2), ('baud_rate',999), ('data_bits',9), ('parity','Bad'),
                              ('stop_bits','None'), ('handshake','Bad')]:
            with self.subTest(column=column), self.assertRaises(sqlite3.IntegrityError):
                self.c.execute(f'UPDATE serial_scanner_config SET {column}=? WHERE id=1', (value,))

    def test_single_row_hardware_configuration(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.c.execute("INSERT INTO serial_scanner_config(id) VALUES(2)")

    def test_previous_settings_remain_intact(self):
        self.assertEqual(self.c.execute("SELECT setting_value FROM app_settings WHERE setting_key='theme'").fetchone()[0], 'light')
        self.assertEqual(self.c.execute("SELECT name FROM products WHERE sku='SKU1'").fetchone()[0], 'قلم')
        self.assertEqual(self.c.execute('PRAGMA integrity_check').fetchone()[0], 'ok')

if __name__ == '__main__': unittest.main()
