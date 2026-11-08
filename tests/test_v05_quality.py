import pathlib
import sqlite3
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = ROOT / 'src' / 'OfflineStore'


class ProfessionalQualityGate(unittest.TestCase):
    def test_database_integrity_checks_at_startup(self):
        s = (SRC / 'Infrastructure' / 'StoreDb.cs').read_text(encoding='utf-8')
        self.assertIn('PRAGMA quick_check;', s)
        self.assertIn('PRAGMA foreign_key_check;', s)

    def test_cashier_cannot_sell_free_when_zero_price(self):
        s = (SRC / 'Services' / 'StoreService.cs').read_text(encoding='utf-8')
        self.assertIn('!mayOverrideSalePrice && line.PriceCents == 0', s)

    def test_sale_cart_prevents_overdraw_and_fractional_pieces(self):
        s = (SRC / 'ViewModels' / 'MainViewModel.cs').read_text(encoding='utf-8')
        self.assertIn('product.IsCountable && qty % 1000 != 0', s)
        self.assertIn('checked(already + qty) > product.StockMilli', s)

    def test_post_uses_confirmation_and_final_transaction(self):
        vm = (SRC / 'ViewModels' / 'MainViewModel.cs').read_text(encoding='utf-8')
        service = (SRC / 'Services' / 'StoreService.cs').read_text(encoding='utf-8')
        self.assertIn('MessageBoxButton.YesNo', vm)
        self.assertIn('if (_posting) return;', vm)
        self.assertIn('using (var tx = c.BeginTransaction())', service)
        self.assertIn('tx.Commit();', service)

    def test_barcode_keyboard_accessibility(self):
        x = (SRC / 'MainWindow.xaml').read_text(encoding='utf-8')
        cs = (SRC / 'MainWindow.xaml.cs').read_text(encoding='utf-8')
        self.assertIn('KeyDown="SaleBarcodeKeyDown"', x)
        self.assertIn('Key.F2', cs)
        self.assertIn('Key.F5', cs)

    def test_readonly_invoice_grids(self):
        x = (SRC / 'App.xaml').read_text(encoding='utf-8')
        self.assertIn('<Setter Property="IsReadOnly" Value="True" />', x)
        self.assertIn('<Setter Property="CanUserDeleteRows" Value="False" />', x)

    def test_all_xaml_is_well_formed(self):
        from xml.etree import ElementTree
        for path in SRC.rglob('*.xaml'):
            with self.subTest(path=path.name):
                ElementTree.parse(str(path))

    def test_domain_test_project_is_present(self):
        self.assertTrue((ROOT/'tests'/'domain'/'DomainTests.csproj').exists())
        self.assertIn('Arabic-Indic', (ROOT/'tests'/'domain'/'Program.cs').read_text(encoding='utf-8'))

    def test_sqlite_supports_quick_check_and_fk_check(self):
        c = sqlite3.connect(':memory:')
        c.execute('PRAGMA foreign_keys=ON')
        c.executescript('CREATE TABLE parent(id INTEGER PRIMARY KEY); CREATE TABLE child(pid INTEGER REFERENCES parent(id));')
        self.assertEqual('ok', c.execute('PRAGMA quick_check').fetchone()[0])
        self.assertFalse(c.execute('PRAGMA foreign_key_check').fetchall())
        c.close()


if __name__ == '__main__':
    unittest.main()
