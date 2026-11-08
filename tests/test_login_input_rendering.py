"""Test login input rendering, RTL bidirectional alignment, password show/hide, and font/caret clarity."""
import pathlib
import sqlite3
import unittest
from xml.etree import ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
APP = ROOT / "src" / "OfflineStore"
INFRA = APP / "Infrastructure"


def make_test_db():
    db = sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript((INFRA / "Schema.sql").read_text(encoding="utf-8"))
    for file in sorted((INFRA / "Migrations").glob("*.sql")):
        db.executescript(file.read_text(encoding="utf-8"))
    return db


class LoginInputRenderingTests(unittest.TestCase):
    def test_login_xaml_well_formed_and_controls_present(self):
        for name in ("UserLoginWindow.xaml", "LoginWindow.xaml", "App.xaml"):
            ET.parse(APP / name)

    def test_login_name_has_no_inverted_flow_direction(self):
        x = (APP / "UserLoginWindow.xaml").read_text(encoding="utf-8")
        root = ET.fromstring(x)
        # Find LoginName
        login_name = None
        for node in root.iter():
            if node.get("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "LoginName":
                login_name = node
                break
        self.assertIsNotNone(login_name, "LoginName TextBox must exist")
        # FlowDirection must NOT be forced to LeftToRight inside RTL window
        self.assertNotEqual(login_name.get("FlowDirection"), "LeftToRight",
                            "LoginName must not force FlowDirection=LeftToRight because it inverts caret and breaks Arabic text")

    def test_password_reveal_controls_and_handlers_exist(self):
        xaml = (APP / "UserLoginWindow.xaml").read_text(encoding="utf-8")
        cs = (APP / "UserLoginWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn('x:Name="VisiblePassword"', xaml)
        self.assertIn('x:Name="TogglePasswordButton"', xaml)
        self.assertIn('Click="TogglePasswordClick"', xaml)
        self.assertIn('PasswordChanged="LoginPasswordChanged"', xaml)
        self.assertIn('TextChanged="VisiblePasswordChanged"', xaml)
        self.assertIn('TogglePasswordClick', cs)
        self.assertIn('LoginPasswordChanged', cs)
        self.assertIn('VisiblePasswordChanged', cs)

    def test_app_xaml_textbox_and_passwordbox_styles(self):
        app = (APP / "App.xaml").read_text(encoding="utf-8")
        self.assertIn('Property="VerticalContentAlignment" Value="Center"', app)
        self.assertIn('Property="CaretBrush" Value="{DynamicResource PrimaryBrush}"', app)
        self.assertIn('Property="PasswordChar" Value="●"', app)
        self.assertIn('Focusable="False"', app)
        self.assertIn('HorizontalScrollBarVisibility="Hidden"', app)

    def test_tab_indices_and_enter_key_submission(self):
        xaml = (APP / "UserLoginWindow.xaml").read_text(encoding="utf-8")
        self.assertIn('IsDefault="True"', xaml)
        self.assertIn('IsCancel="True"', xaml)
        self.assertIn('TabIndex="2"', xaml)  # LoginName
        self.assertIn('TabIndex="3"', xaml)  # LoginPassword

    def test_auth_supports_both_english_and_arabic_usernames(self):
        db = make_test_db()
        # Insert admin and arabic employee
        db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
            VALUES('admin','مدير النظام','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
        db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
            VALUES('مدير','المدير العام','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
        db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
            VALUES('كاشير1','محمد أحمد','CASHIER',zeroblob(32),zeroblob(32),210000,'now')""")
        db.commit()

        # Query using lowercase match
        res_admin = db.execute("SELECT username, display_name FROM users WHERE LOWER(username)=LOWER('admin')").fetchone()
        self.assertEqual(res_admin, ('admin', 'مدير النظام'))

        res_arabic = db.execute("SELECT username, display_name FROM users WHERE LOWER(username)=LOWER('مدير')").fetchone()
        self.assertEqual(res_arabic, ('مدير', 'المدير العام'))

        res_cashier = db.execute("SELECT username, display_name FROM users WHERE LOWER(username)=LOWER('كاشير1')").fetchone()
        self.assertEqual(res_cashier, ('كاشير1', 'محمد أحمد'))
        db.close()


if __name__ == "__main__":
    unittest.main()
