from pathlib import Path
import unittest
ROOT=Path(__file__).resolve().parents[1];APP=ROOT/'src/OfflineStore'
class OperationalRepairTests(unittest.TestCase):
 def text(self,p):return (APP/p).read_text(encoding='utf-8')
 def test_appearance_permission_is_checked_in_service(self):
  self.assertIn('Authorize(_actor, "APPEARANCE")',self.text('Appearance/AppearanceService.cs'))
 def test_supply_approval_refreshes_main_inventory(self):
  self.assertIn('GoodsReceiptPanel.Posted +=',self.text('MainWindow.xaml.cs'))
  self.assertIn('Posted?.Invoke()',self.text('Supply/SupplyVoucherControl.xaml.cs'))
 def test_normal_close_checks_unposted_invoice_lines(self):
  source=self.text('MainWindow.xaml.cs')
  closing=source.split('Closing +=',1)[1].split('Closed +=',1)[0]
  self.assertIn('ConfirmInvoiceChanges()',closing)
 def test_both_installers_offer_directory_and_guard_paths(self):
  for name in ('OfflineStore.nsi','OfflineStore.AllInOne.nsi'):
   s=(ROOT/'installer'/name).read_text(encoding='utf-8-sig')
   self.assertIn('!insertmacro MUI_PAGE_DIRECTORY',s)
   self.assertIn('Call ValidateInstallDirectory',s)
 def test_printing_uses_driver_geometry(self):
  s=self.text('SaleReceiptWindow.cs')
  self.assertIn('GetPrintCapabilities',s)
  self.assertIn('ReceiptPageLayout.ForPrinter',s)
 def test_long_operations_run_off_dispatcher_and_block_reentry(self):
  s=self.text('ViewModels/MainViewModel.cs')
  self.assertIn('private async Task PostAsync',s)
  self.assertIn('await Task.Run',s)
  self.assertIn('if (IsBusy) return;',s)

 def test_nsis_includes_are_relative_to_script_and_sources_are_utf8(self):
  for name in ('OfflineStore.nsi','OfflineStore.AllInOne.nsi'):
   p=ROOT/'installer'/name
   self.assertTrue(p.read_bytes().startswith(b'\xef\xbb\xbf'))
   self.assertIn('!include "InstallPathSafety.nsh"',p.read_text(encoding='utf-8-sig'))

 def test_compressor_is_selected_before_plugin_code_is_embedded(self):
  for name in ('OfflineStore.nsi','OfflineStore.AllInOne.nsi'):
   source=(ROOT/'installer'/name).read_text(encoding='utf-8-sig')
   self.assertLess(source.index('SetCompressor /SOLID lzma'),source.index('!include "InstallPathSafety.nsh"'))

 def test_employee_switch_reuses_one_invoice_confirmation(self):
  source=self.text('MainWindow.xaml.cs').split('private void SwitchEmployeeClick',1)[1].split('private void UpdateAdaptiveLayout',1)[0]
  self.assertEqual(1,source.count('ConfirmInvoiceChanges()'))
  self.assertNotIn('_vm.SaleLines.Count',source)
 def test_restore_file_work_runs_off_dispatcher(self):
  self.assertIn('private async void RestoreBackupClick',self.text('MainWindow.xaml.cs'))
  startup=self.text('StartupRecoveryWindow.cs')
  self.assertIn('apply.Click+=async',startup)
  self.assertIn('await Task.Run',startup)
