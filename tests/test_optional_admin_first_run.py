"""Regression checks for mandatory admin PIN and first-run layout."""
import pathlib
import unittest
from xml.etree import ElementTree

ROOT=pathlib.Path(__file__).resolve().parents[1]
APP=ROOT/"src"/"OfflineStore"
class FirstRunTests(unittest.TestCase):
    def test_scrollable_first_run_ui_and_resizable_window(self):
        x=(APP/"UserLoginWindow.xaml").read_text(encoding="utf8")
        ElementTree.fromstring(x)
        self.assertIn('ResizeMode="CanResizeWithGrip"',x)
        self.assertIn('VerticalScrollBarVisibility="Auto"',x)
        self.assertIn('Grid.Column="1"',x)
        self.assertIn('x:Name="SubmitButton"',x)

    def test_new_admin_requires_pin_and_legacy_admin_gets_no_session(self):
        auth=(APP/"Security/UserAuthService.cs").read_text(encoding="utf8")
        first=auth.split("public void CreateFirstAdmin",1)[1].split("private static void InsertUser",1)[0]
        self.assertIn("ValidatePassword(password);",first)
        self.assertNotIn("if (!string.IsNullOrEmpty(password))",first)
        self.assertIn("throw new LegacyAdminPinRequiredException()",auth)
        self.assertIn("public void ProtectLegacyAdmin",auth)
        self.assertIn('Authenticate(username, "", true)',auth)
        self.assertIn("ResetPassword(legacy, legacy.Id, password)",auth)
        ui=(APP/"UserLoginWindow.xaml.cs").read_text(encoding="utf8")
        self.assertIn("catch (LegacyAdminPinRequiredException)",ui)
        self.assertIn("_auth.ProtectLegacyAdmin",ui)

    def test_existing_password_not_bypassed(self):
        app=(APP/"App.xaml.cs").read_text(encoding="utf8")
        self.assertNotIn('service.Users.Login("admin", "")',app)
        self.assertNotIn('CanAutoLoginAdmin()',app)
        self.assertIn('new UserLoginWindow(service.Users)',app)
        self.assertIn('RememberedLoginStore.TryLoad()',app)
        self.assertIn('if (session.Role == "ADMIN")',app)

    def test_current_admin_session_refresh_after_password_change(self):
        cs=(APP/"UserManagementWindow.xaml.cs").read_text(encoding="utf8")
        self.assertIn('if (selected.Id == _service.CurrentUser.Id)',cs)
        self.assertIn('_service.SignIn(Auth.Login(selected.Username, newPassword))',cs)

    def test_wpf_runtime_captures_first_run_layout(self):
        cs=(APP/"Diagnostics/PreviewScreenshotCapture.cs").read_text(encoding="utf8")
        self.assertIn('00-first-run-login.png',cs)
        self.assertIn('button is clipped',cs)
        self.assertIn('CanAutoLoginAdmin()',cs)
        self.assertIn('92837465',cs)

    def test_installer_version_matches_workflow(self):
        script=(ROOT/"installer/OfflineStore.AllInOne.nsi").read_text(encoding="utf8")
        workflow=(ROOT/".github/workflows/windows-build.yml").read_text(encoding="utf8")
        self.assertIn('!define APP_VERSION "0.15.0"',script)
        self.assertIn('OfflineStore-Setup-v0.15.0-ALL-IN-ONE.exe',workflow)
        self.assertIn('Expected 17 incl. accounting ledger, employee permissions and receipt previews',workflow)
if __name__=="__main__":
    unittest.main()
