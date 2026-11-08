"""v11 employee-specific grants: SQL migration, UI contract and security regressions."""
import pathlib
import sqlite3
import unittest
from xml.etree import ElementTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
APP=ROOT/"src"/"OfflineStore"
INFRA=APP/"Infrastructure"
SQL=INFRA/"Migrations/011_employee_permissions.sql"

def old_database():
    db=sqlite3.connect(":memory:")
    db.execute("PRAGMA foreign_keys=ON")
    db.executescript((INFRA/"Schema.sql").read_text(encoding="utf-8"))
    for file in sorted((INFRA/"Migrations").glob("*.sql")):
        if file.name.startswith("011_"): continue
        db.executescript(file.read_text(encoding="utf-8"))
    db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
        VALUES('admin','مدير','ADMIN',zeroblob(32),zeroblob(32),210000,'now')""")
    db.execute("""INSERT INTO users(username,display_name,role,salt,password_hash,iterations,created_utc)
        VALUES('seller','كاشير','CASHIER',zeroblob(32),zeroblob(32),210000,'now')""")
    db.commit()
    return db

class EmployeePermissionsTests(unittest.TestCase):
    def setUp(self):
        self.db=old_database()
        self.db.executescript(SQL.read_text(encoding="utf-8"))
    def tearDown(self):
        self.db.close()

    def test_migration_does_not_change_existing_role_or_login(self):
        self.assertEqual(
            [('admin','ADMIN',1),('seller','CASHIER',1)],
            self.db.execute("SELECT username,role,active FROM users ORDER BY id").fetchall()
        )
        self.assertEqual(0,self.db.execute(
            "SELECT COUNT(*) FROM user_permission_overrides").fetchone()[0])
        self.assertEqual(len(list((INFRA/"Migrations").glob("*.sql"))), 23)

    def test_grant_revoke_and_reject_admin_privilege(self):
        self.db.execute("""INSERT INTO user_permission_overrides
            (user_id,permission,allowed,changed_by_user_id,changed_utc)
            VALUES(2,'REPORTS',1,1,'now')""")
        self.db.execute("""INSERT INTO user_permission_overrides
            (user_id,permission,allowed,changed_by_user_id,changed_utc)
            VALUES(2,'SALE',0,1,'now')""")
        self.assertEqual(
            [('REPORTS',1),('SALE',0)],
            self.db.execute("""SELECT permission,allowed FROM user_permission_overrides
                WHERE user_id=2 ORDER BY permission""").fetchall()
        )
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("""INSERT INTO user_permission_overrides
                (user_id,permission,allowed,changed_by_user_id,changed_utc)
                VALUES(2,'USERS',1,1,'now')""")
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("""INSERT INTO user_permission_overrides
                (user_id,permission,allowed,changed_by_user_id,changed_utc)
                VALUES(2,'BACKUP',3,1,'now')""")
        with self.assertRaisesRegex(sqlite3.IntegrityError,"ADMIN_PERMISSIONS_FIXED"):
            self.db.execute("""INSERT INTO user_permission_overrides
                (user_id,permission,allowed,changed_by_user_id,changed_utc)
                VALUES(1,'BACKUP',0,1,'now')""")
        with self.assertRaises(sqlite3.IntegrityError):
            self.db.execute("""INSERT INTO user_permission_overrides
                (user_id,permission,allowed,changed_by_user_id,changed_utc)
                VALUES(777,'SALE',1,1,'now')""")

    def test_permission_check_is_from_db_not_cached_role(self):
        service=(APP/"Security/UserAuthService.cs").read_text(encoding="utf-8")
        self.assertIn("SELECT role,password_hash FROM users WHERE id=@id AND active=1",service)
        self.assertIn("SecureEqual(session.CredentialProof, currentHash)",service)
        self.assertIn('if (permission == "USERS") return false;',service)
        self.assertIn('SELECT allowed FROM user_permission_overrides WHERE user_id=@id',service)
        self.assertIn("RoleCan(role, permission)",service)
        self.assertIn("ValidatePermissionSet(permissions)",service)
        self.assertIn("ReplacePermissions(c, tx,",service)
        self.assertIn("USER_PERMISSIONS_CHANGED",service)
        self.assertIn("tx.Commit();",service)
        self.assertIn('if (role == "ADMIN")',service)
        self.assertIn("RequireAdmin(actor);",service)

    def test_ui_has_per_employee_permissions_and_creation(self):
        window=(APP/"UserManagementWindow.xaml").read_text(encoding="utf-8")
        ElementTree.fromstring(window)
        for name in ("AllowSale","AllowPurchase","AllowCatalog","AllowReports",
                     "AllowBackup","AllowLowStock","AllowOverridePrice","AllowAppearance"):
            self.assertIn(f'x:Name="{name}"',window)
        self.assertIn('Click="SavePermissionsClick"',window)
        self.assertIn('Click="CreateUserClick"',window)
        self.assertIn('Click="NewUserClick"',window)
        self.assertIn('SelectionChanged="UsersSelectionChanged"',window)
        code=(APP/"UserManagementWindow.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("Auth.EffectivePermissions(",code)
        self.assertIn("Auth.SetUserPermissions(",code)
        self.assertIn("Auth.CreateUserWithPermissions(",code)
        self.assertIn('role=="ADMIN" ? null : CheckedPermissions()',code)

    def test_upgrade_safety_and_backup_compatibility(self):
        store=(INFRA/"StoreDb.cs").read_text(encoding="utf-8")
        self.assertIn('if (version == 24) { VerifyIntegrity(connection); return; }',store)
        self.assertIn('EmbeddedSql("OfflineStore.Infrastructure.Migrations.011_employee_permissions.sql")',store)
        self.assertIn('"store-before-v11-"',store)
        recovery=(INFRA/"SqliteRecoveryService.cs").read_text(encoding="utf-8")
        self.assertIn("found != 9 && found != 10 && found != 11",recovery)
        csproj=(APP/"OfflineStore.csproj").read_text(encoding="utf-8")
        self.assertIn('011_employee_permissions.sql',csproj)

    def test_windows_qa_covers_same_session_revoke(self):
        qa=(APP/"Diagnostics/PreviewScreenshotCapture.cs").read_text(encoding="utf-8")
        self.assertIn("CreateUserWithPermissions(",qa)
        self.assertIn("SetUserPermissions(",qa)
        self.assertIn('users.Authorize(customSession, "SALE")',qa)
        self.assertIn('users.Authorize(customSession, "USERS")',qa)
        self.assertIn('"15-employee-permissions.png"',qa)

if __name__=="__main__":
    unittest.main()
