"""Customer receipt snapshots, QR/LAN boundaries and offline installer regressions."""
import pathlib
import sqlite3
import unittest
from xml.etree import ElementTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
APP=ROOT/"src"/"OfflineStore"
INFRA=APP/"Infrastructure"

def database(migrate_v10=True):
    db=sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript((INFRA/"Schema.sql").read_text(encoding="utf8"))
    for path in sorted((INFRA/"Migrations").glob("*.sql")):
        if not migrate_v10 and path.name.startswith("010_"): continue
        db.executescript(path.read_text(encoding="utf8"))
    db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
        VALUES('admin','مدير','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
    db.execute("""INSERT INTO products(sku,name,unit,created_utc,last_cost_cents,avg_cost_millicents)
        VALUES('TEA','الشاي الأصلي','قطعة','now',500,500000)""")
    db.execute("""INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
        VALUES(1,'قطعة',1000,1500,1)""")
    db.execute("INSERT INTO parties(party_type,name,created_utc) VALUES('SUPPLIER','مورد','now')")
    db.commit()
    return db

def post(db,kind,amount):
    partner=1 if kind=="PURCHASE" else None
    doc=db.execute(
        "INSERT INTO documents(doc_type,party_id,total_cents,created_utc,created_by_user_id) "
        "VALUES(?,?,?,'now',1)",(kind,partner,amount)
    ).lastrowid
    line=db.execute("""INSERT INTO document_lines(
        document_id,line_no,product_id,qty_milli,unit_price_cents,line_total_cents,
        unit_cost_snapshot_cents,product_unit_id,entered_qty_milli,entered_unit_name,
        unit_factor_milli,entered_unit_price_cents)
        VALUES(?,1,1,1000,?,?,500,1,1000,'قطعة',1000,?)""",
        (doc,amount,amount,amount)).lastrowid
    if kind=="SALE" and db.execute(
        "SELECT 1 FROM sqlite_master WHERE name='sale_line_names'"
    ).fetchone():
        db.execute("""INSERT INTO sale_line_names(
            document_line_id,product_id,name_at_sale,snapshot_source)
            VALUES(?,1,'الشاي الأصلي','POSTED')""",(line,))
    db.execute(
        "INSERT INTO stock_movements(document_id,document_line_id,product_id,delta_qty_milli,created_utc) "
        "VALUES(?,?,1,?,'now')",(doc,line,1000 if kind=="PURCHASE" else -1000)
    )
    db.execute(
        "UPDATE documents SET doc_code=?,state='POSTED' WHERE id=?",
        (("P-" if kind=="PURCHASE" else "S-")+f"{doc:06d}",doc)
    )
    return line

class ReceiptRegressionTests(unittest.TestCase):
    def test_posted_invoice_name_immutable_after_product_edit(self):
        db=database()
        try:
            post(db,"PURCHASE",500)
            line=post(db,"SALE",1500)
            db.execute("UPDATE products SET name='الشاي الجديد' WHERE id=1")
            self.assertEqual(("الشاي الأصلي","POSTED"),db.execute(
                "SELECT name_at_sale,snapshot_source FROM sale_line_names WHERE document_line_id=?",
                (line,)).fetchone())
            with self.assertRaisesRegex(sqlite3.IntegrityError,"SALE_NAME_IMMUTABLE"):
                db.execute("UPDATE sale_line_names SET name_at_sale='مزوّر' WHERE document_line_id=?",
                           (line,))
            with self.assertRaisesRegex(sqlite3.IntegrityError,"SALE_NAME_IMMUTABLE"):
                db.execute("DELETE FROM sale_line_names WHERE document_line_id=?",(line,))
        finally:
            db.close()

    def test_legacy_name_migrates_without_overwriting_invoice(self):
        db=database(migrate_v10=False)
        try:
            post(db,"PURCHASE",500)
            line=post(db,"SALE",1500)
            old=db.execute("SELECT doc_code,total_cents FROM documents WHERE doc_type='SALE'").fetchone()
            db.executescript((INFRA/"Migrations/010_sale_line_names.sql").read_text(encoding="utf8"))
            self.assertEqual(("الشاي الأصلي","MIGRATED"),db.execute(
                "SELECT name_at_sale,snapshot_source FROM sale_line_names WHERE document_line_id=?",
                (line,)).fetchone())
            self.assertEqual(old,db.execute(
                "SELECT doc_code,total_cents FROM documents WHERE doc_type='SALE'").fetchone())
        finally:
            db.close()

    def test_post_saves_snapshot_inside_original_transaction(self):
        cs=(APP/"Services/StoreService.cs").read_text(encoding="utf8")
        section=cs.split("public string Post(",1)[1].split("private static void CheckBarcode",1)[0]
        self.assertIn("INSERT INTO sale_line_names",section)
        self.assertIn("SELECT @p0,id,name,'POSTED' FROM products",section)
        self.assertIn("tx.Commit();",section)

    def test_qr_image_is_not_an_internet_url(self):
        server=(APP/"Services/OfflineReceiptServer.cs").read_text(encoding="utf8")
        self.assertIn("NetworkInterfaceType.Wireless80211",server)
        self.assertIn("NetworkInterfaceType.Ethernet",server)
        self.assertIn("new TcpListener(ip, Port)",server)
        self.assertIn("HMACSHA256",server)
        self.assertIn("Equal(route[3],Signature(code))",server)
        self.assertIn("Content-Type: image/svg+xml",server)
        self.assertIn("Content-Security-Policy:",server)
        self.assertIn("WriteResponse(stream,404",server)
        self.assertNotIn("https://",server)
        receipt=(APP/"Services/SaleReceiptService.cs").read_text(encoding="utf8")
        self.assertIn("AND d.state='POSTED'",receipt)
        self.assertIn("s.name_at_sale",receipt)
        self.assertIn("SecurityElement.Escape",receipt)

    def test_invoice_window_is_real_wpf_and_offline(self):
        code=(APP/"SaleReceiptWindow.cs").read_text(encoding="utf8")
        self.assertIn("dialog.PrintDocument(",code)
        self.assertIn("PngByteQRCode",code)
        self.assertIn("SavePngClick",code)
        self.assertIn("SaveSvgClick",code)
        self.assertIn("QR يحتوي",code)
        window=(APP/"MainWindow.xaml").read_text(encoding="utf8")
        ElementTree.fromstring(window)
        self.assertIn('Click="OpenSelectedReceiptClick"',window)
        self.assertIn('Command="{Binding EditSaleLineCommand}"',window)
        self.assertIn('Command="{Binding EditPurchaseLineCommand}"',window)

    def test_setup_english_without_garbled_arabic_font(self):
        for path in ("installer/OfflineStore.nsi","installer/OfflineStore.AllInOne.nsi"):
            text=(ROOT/path).read_text(encoding="utf-8-sig")
            self.assertIn('!insertmacro MUI_LANGUAGE "English"',text)
            self.assertNotIn('MUI_LANGUAGE "Arabic"',text)
            self.assertNotIn("مبيعات ومخازن",text)
        x=(ROOT/"installer/OfflineStore.AllInOne.nsi").read_text(encoding="utf8")
        self.assertIn("remoteip=localsubnet",x)
        self.assertIn("profile=private",x)

if __name__=="__main__":
    unittest.main()
