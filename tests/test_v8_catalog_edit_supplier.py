"""Catalog supplier links and edit guards tested against deployed SQLite schema."""
import pathlib
import sqlite3
import unittest
from xml.etree import ElementTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
INFRA=ROOT/"src"/"OfflineStore"/"Infrastructure"
APP=ROOT/"src"/"OfflineStore"

def db():
    c=sqlite3.connect(":memory:")
    c.execute("PRAGMA foreign_keys=ON")
    c.executescript((INFRA/"Schema.sql").read_text(encoding="utf-8"))
    for p in sorted((INFRA/"Migrations").glob("*.sql")):
        c.executescript(p.read_text(encoding="utf-8"))
    c.execute("INSERT INTO products(sku,name,created_utc) VALUES('TEA','شاي','now')")
    c.execute("INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base) VALUES(1,'قطعة',1000,1500,1)")
    c.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','مورد أول','now')")
    c.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('CUSTOMER','عميل','now')")
    c.commit()
    return c

class CatalogEditingTests(unittest.TestCase):
    def setUp(self): self.c=db()
    def tearDown(self): self.c.close()

    def test_supplier_association_optional(self):
        self.assertEqual([],self.c.execute("SELECT * FROM product_preferred_suppliers").fetchall())
        self.assertEqual(0,self.c.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0])

    def test_supplier_link_does_not_change_inventory_or_invoices(self):
        self.c.execute("INSERT INTO product_preferred_suppliers VALUES (1,1,'now')")
        self.assertEqual(0,self.c.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0])
        self.assertEqual(0,self.c.execute("SELECT COUNT(*) FROM documents").fetchone()[0])

    def test_customer_cannot_be_catalog_supplier(self):
        with self.assertRaisesRegex(sqlite3.IntegrityError,"NOT_A_SUPPLIER"):
            self.c.execute("INSERT INTO product_preferred_suppliers VALUES(1,2,'now')")

    def test_nonexistent_supplier_rejected(self):
        with self.assertRaises(sqlite3.IntegrityError):
            self.c.execute("INSERT INTO product_preferred_suppliers VALUES(1,999,'now')")

    def test_supplier_update_guard(self):
        self.c.execute("INSERT INTO product_preferred_suppliers VALUES (1,1,'now')")
        with self.assertRaisesRegex(sqlite3.IntegrityError,"NOT_A_SUPPLIER"):
            self.c.execute("UPDATE product_preferred_suppliers SET supplier_id=2 WHERE product_id=1")

    def test_catalog_name_and_price_update_leave_base_unit_and_stock_consistent(self):
        self.c.execute("UPDATE products SET name='شاي أحمر',sale_price_cents=1600 WHERE id=1")
        self.c.execute("UPDATE product_units SET sale_price_cents=1600 WHERE product_id=1 AND is_base=1")
        self.assertEqual(("شاي أحمر",1600),self.c.execute("SELECT name,sale_price_cents FROM products WHERE id=1").fetchone())
        self.assertEqual(1600,self.c.execute("SELECT sale_price_cents FROM product_units WHERE product_id=1").fetchone()[0])
        self.assertEqual(0,self.c.execute("SELECT qty_milli FROM v_stock WHERE product_id=1").fetchone()[0])

    def test_service_does_not_edit_original_stock_movement_or_invoice(self):
        s=(APP/"Services"/"StoreService.cs").read_text(encoding="utf-8")
        section=s[s.index("public void UpdateProduct"):s.index("public void AddProduct")]
        self.assertIn('Require("CATALOG")',section)
        self.assertIn("SetPreferredSupplier(c, tx",section)
        self.assertIn("tx.Commit()",section)
        self.assertNotIn("UPDATE documents",section)
        self.assertNotIn("UPDATE stock_movements",section)

    def test_catalog_edit_preserves_fractional_average_with_stock(self):
        # Run the actual SQL from StoreService.UpdateProduct against SQLite,
        # rather than reimplementing its CASE logic in the test.
        source=(APP/"Services"/"StoreService.cs").read_text(encoding="utf-8")
        section=source.split("public void UpdateProduct",1)[1].split("public void AddProduct",1)[0]
        self.assertIn('using (var update = Cmd(c, tx, @"UPDATE products SET', section)
        sql=section.split('using (var update = Cmd(c, tx, @"',1)[1].split('",',1)[0]
        self.assertIn("WHERE id=@p6 AND active=1",sql)
        self.assertIn("stockMilli = reader.GetInt64(0)",section)
        self.c.execute(
            "UPDATE products SET avg_cost_millicents=1020833,last_cost_cents=1021 WHERE id=1")
        inputs=dict(p0="TEA",p1="شاي معدل",p2=None,p3=1750,p4=1021,
                    p5=5000,p6=1,p7=1000)
        self.c.execute(sql,inputs)
        self.assertEqual(
            (1020833,1021,1750),
            self.c.execute(
                "SELECT avg_cost_millicents,last_cost_cents,sale_price_cents "
                "FROM products WHERE id=1").fetchone())
        # When no stock exists, the manager can set a fresh initial cost.
        inputs.update(p4=1300,p7=0)
        self.c.execute(sql,inputs)
        self.assertEqual(
            (1300000,1300),
            self.c.execute(
                "SELECT avg_cost_millicents,last_cost_cents FROM products WHERE id=1"
            ).fetchone())

    def test_optional_vendor_new_or_existing_follows_transaction(self):
        s=(APP/"Services"/"StoreService.cs").read_text(encoding="utf-8")
        self.assertIn("ORDER BY id LIMIT 1",s)
        self.assertIn("INSERT INTO product_preferred_suppliers",s)
        self.assertIn("SUPPLIER_CREATED",s)

    def test_home_screen_is_direct_with_explicit_password_exception(self):
        s=(APP/"App.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('if (service.CurrentUser == null)',s)
        self.assertNotIn('CreateFirstAdmin("مدير النظام", "")',s)
        self.assertNotIn('if (service.Users.CanAutoLoginAdmin())',s)
        self.assertIn('new UserLoginWindow(service.Users)',s)

    def test_xaml_can_parse_and_navigates_left(self):
        x=(APP/"MainWindow.xaml").read_text(encoding="utf-8")
        ElementTree.fromstring(x)
        self.assertIn('TabStripPlacement="Left"',x)
        self.assertIn('Command="{Binding SaveProductChangesCommand}"',x)
        self.assertIn('Text="{Binding CatalogSupplierText',x)
        self.assertIn('Click="GoSalesClick"',x)
        self.assertIn('IsEnabled="{Binding IsUnitEditable}"',x)

    def test_reject_associating_multiple_suppliers_as_preferred(self):
        self.c.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','ثاني','now')")
        self.c.execute("INSERT INTO product_preferred_suppliers VALUES (1,1,'now')")
        with self.assertRaises(sqlite3.IntegrityError):
            self.c.execute("INSERT INTO product_preferred_suppliers VALUES(1,3,'now')")

if __name__=="__main__":
    unittest.main()
