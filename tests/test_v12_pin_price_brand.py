"""v12 retail/semi/wholesale + PIN, DPAPI login and icon regression checks."""
import pathlib
import sqlite3
import struct
import unittest
from xml.etree import ElementTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
APP=ROOT/"src/OfflineStore"
INFRA=APP/"Infrastructure"
PRICE_SQL=INFRA/"Migrations/012_price_levels.sql"

def make_v11_db():
    db=sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript((INFRA/"Schema.sql").read_text(encoding="utf-8"))
    for file in sorted((INFRA/"Migrations").glob("*.sql")):
        if file.name.startswith("012_"):
            continue
        db.executescript(file.read_text(encoding="utf-8"))
    db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
                 VALUES('admin','المدير','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
    db.execute("""INSERT INTO products(sku,name,unit,sale_price_cents,created_utc)
                 VALUES('TEA','شاي','قطعة',1500,'now')""")
    # Existing v1/v3 triggers may require a corresponding product unit.
    db.execute("""INSERT INTO product_units(product_id,name,factor_milli,sale_price_cents,is_base)
                 VALUES(1,'قطعة',1000,1500,1)""")
    db.commit()
    return db

class PricePinBrandTests(unittest.TestCase):
    def setUp(self):
        self.db=make_v11_db()
        self.db.executescript(PRICE_SQL.read_text(encoding="utf-8"))
    def tearDown(self):
        self.db.close()

    def test_old_retail_price_and_historical_invoice_schema_preserved(self):
        self.assertEqual(self.db.execute("SELECT sale_price_cents FROM product_units WHERE id=1").fetchone()[0],1500)
        self.assertEqual(0,self.db.execute("SELECT COUNT(*) FROM unit_price_levels").fetchone()[0])
        self.assertIn("sale_price_level",[r[1] for r in self.db.execute("PRAGMA table_info(document_lines)")])
        self.assertEqual(len(list((INFRA/"Migrations").glob("*.sql"))), 23)

    def test_tier_price_is_constrained_and_bound_to_unit(self):
        self.db.execute("""INSERT INTO unit_price_levels(product_unit_id,price_level,price_cents,updated_by_user_id,updated_utc)
                VALUES(1,'SEMI',1400,1,'now')""")
        self.db.execute("""INSERT INTO unit_price_levels(product_unit_id,price_level,price_cents,updated_by_user_id,updated_utc)
                VALUES(1,'WHOLESALE',1200,1,'now')""")
        self.assertEqual([('SEMI',1400),('WHOLESALE',1200)],
            self.db.execute("SELECT price_level,price_cents FROM unit_price_levels ORDER BY price_level").fetchall())
        for level, price, uid in [('STAFF',1100,1),('SEMI',-1,1),('SEMI',1300,999),
                                  ('RETAIL',1500,1)]:
            with self.assertRaises(sqlite3.IntegrityError):
                self.db.execute("""INSERT INTO unit_price_levels
                    (product_unit_id,price_level,price_cents,updated_by_user_id,updated_utc)
                    VALUES(1,?,?,?,'now')""",(level,price,uid))
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("""INSERT INTO unit_price_levels
                (product_unit_id,price_level,price_cents,updated_by_user_id,updated_utc)
                VALUES(999,'WHOLESALE',1,1,'now')""")

    def test_sale_uses_db_price_and_tier_snapshot(self):
        source=(APP/"Services/StoreService.cs").read_text(encoding="utf-8")
        self.assertIn("void SetAllUnitPrices",source)
        self.assertIn("Require(\"CATALOG\")",source)
        self.assertIn("price_level='SEMI'",source)
        self.assertIn("price_level='WHOLESALE'",source)
        self.assertIn("line.PriceLevel ?? \"RETAIL\"",source)
        self.assertIn("!mayOverrideSalePrice && line.PriceCents != listedPrice",source)
        self.assertIn("sale_price_level)",source)
        self.assertIn("UNIT_PRICE_TIERS_CHANGED",source)
        models=(APP/"Models/Records.cs").read_text(encoding="utf-8")
        self.assertIn("PriceForLevel(string level)",models)
        self.assertIn("public string PriceLevel",models)

    def test_windows_pin_and_dpapi_protected_remembered_login(self):
        code=(APP/"Security/UserAuthService.cs").read_text(encoding="utf-8")
        self.assertIn('@"^[0-9]{6,12}$"',code)
        self.assertIn("if (IsAdminPasswordless(actor))",code)
        self.assertIn("public bool CanAutoLoginAdmin() { return false; }",code)
        self.assertIn('ValidatePassword(password);',code)
        self.assertIn("locked_until_utc",code)
        auth=(APP/"Security/RememberedLoginStore.cs").read_text(encoding="utf-8")
        self.assertIn("ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser)",auth)
        self.assertIn("ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser)",auth)
        self.assertIn("File.Replace(temp, FilePath, null)",auth)
        self.assertNotIn("WriteAllText",auth)
        app=(APP/"App.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("RememberedLoginStore.TryLoad()",app)
        self.assertIn("service.Users.Login(remembered.Username, remembered.Password)",app)
        self.assertIn('if (session.Role == "ADMIN")',app)

    def test_wpf_password_and_price_tier_controls(self):
        for filename in ("UserLoginWindow.xaml","UserManagementWindow.xaml","MainWindow.xaml"):
            ElementTree.parse(APP/filename)
        login=(APP/"UserLoginWindow.xaml").read_text(encoding="utf-8")
        staff=(APP/"UserManagementWindow.xaml").read_text(encoding="utf-8")
        sales=(APP/"MainWindow.xaml").read_text(encoding="utf-8")
        self.assertIn('x:Name="RememberLoginBox"',login)
        self.assertIn('x:Name="VisiblePinInput"',staff)
        self.assertIn('x:Name="ShowPinBox"',staff)
        self.assertIn('Click="SwitchEmployeeClick"',sales)
        for value in ("RETAIL","SEMI","WHOLESALE"):
            self.assertIn(f'Tag="{value}"',sales)
        for bind in ("UnitSemiPrice","UnitWholesalePrice","SalePriceTier"):
            self.assertIn(bind,sales)

    def test_real_multi_size_windows_icon_is_bundled(self):
        icon=(APP/"Assets/OfflineStore.ico").read_bytes()
        self.assertGreater(len(icon),10000)
        reserved,kind,count=struct.unpack_from("<HHH",icon,0)
        self.assertEqual((0,1,6),(reserved,kind,count))
        dims=[icon[6+i*16] for i in range(count)]
        self.assertIn(16,dims)
        self.assertIn(32,dims)
        self.assertIn(128,dims)
        cs=(APP/"OfflineStore.csproj").read_text(encoding="utf-8")
        self.assertIn("<ApplicationIcon>Assets\\OfflineStore.ico</ApplicationIcon>",cs)
        for path in ("OfflineStore.AllInOne.nsi","OfflineStore.nsi"):
            self.assertIn('Icon "..\\src\\OfflineStore\\Assets\\OfflineStore.ico"',
                          (ROOT/"installer"/path).read_text(encoding="utf-8"))

    def test_schema_backup_and_windows_runtime_qa(self):
        db=(INFRA/"StoreDb.cs").read_text(encoding="utf-8")
        self.assertIn("if (version == 24) { VerifyIntegrity(connection); return; }",db)
        self.assertIn('"store-before-v12-"',db)
        self.assertIn("012_price_levels.sql",db)
        recovery=(INFRA/"SqliteRecoveryService.cs").read_text(encoding="utf-8")
        self.assertIn("found != 11 && found != 12",recovery)
        qa=(APP/"Diagnostics/PreviewScreenshotCapture.cs").read_text(encoding="utf-8")
        self.assertIn("SetAllUnitPrices(",qa)
        self.assertIn("RememberedLoginStore.Save(",qa)
        self.assertIn("WHOLESALE",qa)

if __name__=="__main__": unittest.main()
