"""WPF premium UI contract: layout changes must not detach existing shop actions."""
import pathlib
import re
import unittest
from xml.etree import ElementTree as ET

ROOT=pathlib.Path(__file__).resolve().parents[1]
APP=ROOT/"src/OfflineStore"
PRES="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
X="http://schemas.microsoft.com/winfx/2006/xaml"

class PremiumUiContractTests(unittest.TestCase):
    def text(self,name):
        return (APP/name).read_text(encoding="utf-8")
    def test_all_xaml_documents_are_well_formed(self):
        for filename in ("App.xaml","MainWindow.xaml","UserLoginWindow.xaml",
                         "UserManagementWindow.xaml","Supply/SupplyVoucherControl.xaml",
                         "SerialScannerSettingsWindow.xaml","SecurityWindow.xaml","LoginWindow.xaml"):
            with self.subTest(filename=filename):
                ET.parse(APP/filename)

    def test_navigation_has_exactly_ten_existing_pages_and_no_fake_modules(self):
        root=ET.parse(APP/"MainWindow.xaml").getroot()
        tabs=root.findall(".//{%s}TabItem"%PRES)
        self.assertEqual(10,len(tabs))
        labels=[t.get("Header") for t in tabs]
        self.assertEqual(
            ["الرئيسية","الأصناف","الموردون","المشتريات","المبيعات (F2)",
             "الفواتير","ملحوظات","الإعدادات","حركات المخزون","توريد واستلام"],
            labels)
        for t in tabs:
            self.assertTrue(t.get("Tag"),f"Missing navigation icon on {t.get('Header')}")
        for name in ("DashboardTab","ProductsTab","PurchasesTab","SalesTab",
                     "NotesTab","GoodsReceiptTab"):
            self.assertEqual(1,sum(t.get("{%s}Name"%X)==name for t in tabs))
        self.assertEqual(
            0,sum(1 for t in tabs if any(word in t.get("Header","")
                for word in ("مرتجعات","الخزنة","المديونيات","الجرد","تحصيل"))))

    def test_dashboard_uses_existing_real_data_not_fabricated_revenue(self):
        source=self.text("MainWindow.xaml")
        for binding in ("ProductCount","SupplierCount","LowStockCount","LowStockBanner"):
            self.assertIn("Binding "+binding,source)
        self.assertIn('ItemsSource="{Binding LowStock}"',source)
        for fake in ('Text="12,600.00"','Text="7,900.00"','Text="2,480.00"'):
            self.assertNotIn(fake,source)
        for action in ("GoSalesClick","GoProductsClick","GoPurchasesClick",
                       "GoGoodsReceiptClick","GoNotesClick"):
            self.assertIn('Click="%s"'%action,source)
        self.assertIn("MainWindow_SizeChanged" if False else "UpdateAdaptiveLayout",
                      self.text("MainWindow.xaml.cs"))

    def test_controls_preserved_and_event_handlers_resolve(self):
        files=[
          ("MainWindow.xaml",self.text("MainWindow.xaml.cs")+
                             self.text("MainWindow.Enhancements.cs")),
          ("UserLoginWindow.xaml",self.text("UserLoginWindow.xaml.cs")),
          ("UserManagementWindow.xaml",self.text("UserManagementWindow.xaml.cs")),
          ("Supply/SupplyVoucherControl.xaml",self.text("Supply/SupplyVoucherControl.xaml.cs")),
          ("SerialScannerSettingsWindow.xaml",self.text("SerialScannerSettingsWindow.xaml.cs")),
          ("SecurityWindow.xaml",self.text("SecurityWindow.xaml.cs")),
          ("LoginWindow.xaml",self.text("LoginWindow.xaml.cs")),
        ]
        events={"Click","TextChanged","SelectionChanged","SelectedDateChanged",
                "MouseDoubleClick","MouseLeftButtonUp","KeyDown","PreviewKeyDown",
                "Checked","Unchecked","PasswordChanged"}
        for name,cs in files:
            root=ET.parse(APP/name).getroot()
            with self.subTest(page=name):
                for node in root.iter():
                    for prop,value in node.attrib.items():
                        if prop not in events: continue
                        # Names must be actual C# handlers, not merely design placeholders.
                        self.assertRegex(cs,r"\b"+re.escape(value)+r"\s*\(")

    def test_existing_user_admin_security_controls_remain(self):
        staff=self.text("UserManagementWindow.xaml")
        for control in ("UsersGrid","UsernameInput","NameInput","RoleInput",
                        "PasswordInput","VisiblePinInput","ShowPinBox",
                        "PermissionsPanel","AllowSale","AllowPurchase","AllowCatalog",
                        "AllowReports","AllowBackup","AllowLowStock",
                        "AllowOverridePrice","AllowAppearance"):
            self.assertIn('x:Name="%s"'%control,staff)
        for action in ("CreateUserClick","SavePermissionsClick","ResetClick",
                       "ToggleClick","NewUserClick"):
            self.assertIn('Click="%s"'%action,staff)
        login=self.text("UserLoginWindow.xaml")
        for control in ("AdminName","LoginName","LoginPassword","ConfirmPassword",
                        "RememberLoginBox","ErrorMessage","SubmitButton","PageTitle"):
            self.assertIn('x:Name="%s"'%control,login)

    def test_design_system_and_saved_dark_mode_both_work(self):
        app=self.text("App.xaml")
        theme=self.text("Appearance/AppearanceService.cs")
        for brush in ("PrimaryBrush","HeaderBrush","BaseBrush","SurfaceBrush",
                      "SurfaceAltBrush","TextBrush","BorderBrush","GridAltBrush",
                      "NavRailBrush","BrandPanelBrush","PrimaryGradientBrush",
                      "BlueMetricBrush","GreenMetricBrush","PurpleMetricBrush",
                      "SelectedRowBrush","PositiveBrush","DangerBrush"):
            self.assertIn('x:Key="%s"'%brush,app)
            self.assertIn('resources["%s"]'%brush,theme)
        for style in ("SurfaceCard","MetricCard","SectionHeading","OutlineButton",
                      "SuccessButton","DangerButton"):
            self.assertIn('x:Key="%s"'%style,app)
        self.assertIn('<Setter Property="IsReadOnly" Value="True" />',app)
        self.assertIn("Theme == \"dark\"",theme)
        self.assertIn('x:Name="BrandColumn"',self.text("MainWindow.xaml"))
        self.assertIn('Source="{x:Static brand:BrandAssets.StoreIcon}"',self.text("MainWindow.xaml"))
        self.assertIn('IconBitmapDecoder',self.text("Appearance/BrandAssets.cs"))
        self.assertIn('OrderByDescending',self.text("Appearance/BrandAssets.cs"))
        self.assertIn('<Resource Include="Assets\\OfflineStore.ico" />',self.text("OfflineStore.csproj"))

    def test_no_business_logic_changes_required_for_design(self):
        xaml=self.text("MainWindow.xaml")
        for existing in ('x:Name="CatalogProductsGrid"','x:Name="SaleBarcodeInput"',
                         'x:Name="NotesEditor"','x:Name="SaleStockHint"' if False
                         else 'Text="{Binding SaleStockHint}"'):
            self.assertIn(existing,xaml)
        for c in ("PostSaleCommand","PostPurchaseCommand","BackupCommand",
                  "CreateProductCommand","SaveProductChangesCommand",
                  "AddPurchaseLineCommand","AddSaleLineCommand","SetUnitPriceCommand"):
            self.assertIn('Command="{Binding %s}"'%c,xaml)

if __name__=="__main__": unittest.main()
