using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using OfflineStore.Devices;
using OfflineStore.Security;
using OfflineStore.Services;
using OfflineStore.ViewModels;

namespace OfflineStore
{
    public partial class MainWindow : Window
    {
        private readonly StoreService _service;
        private readonly MainViewModel _vm;
        private SerialBarcodeScanner _scanner;
        private bool _restoringDatabase;
        private bool _preparingRestore;
        private bool _switchingUser;
        private bool _windowClosed;
        private int _backupRunning;
        private DispatcherTimer _backupTimer;
        private DispatcherTimer _clockTimer;
        private OfflineReceiptServer _receiptServer;

        public MainWindow(StoreService service)
        {
            _service = service;
            InitializeComponent();
            _vm = new MainViewModel(service);
            DataContext = _vm;
            _vm.SalePosted += code => ShowSaleReceipt(code);
            GoodsReceiptPanel.Posted += () => _vm.RefreshCommand.Execute(null);
            if (_service.Can("PURCHASE")) GoodsReceiptPanel.Setup(_service);
            Loaded += (s, e) =>
            {
                DashboardTab.IsSelected = true;
                StartScanner(); RefreshNotes(); LoadNote(null);
                StartBackupScheduler(); StartReceiptSharing();
                UpdateClock();
                _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                _clockTimer.Tick += (sender, args) => UpdateClock();
                _clockTimer.Start();
                UpdateAdaptiveLayout(ActualWidth);
            };
            SizeChanged += (s, e) => UpdateAdaptiveLayout(e.NewSize.Width);
            PreviewKeyDown += MainWindow_PreviewKeyDown;
#if !SCREENSHOT_BUILD
            Closing += (s, e) => { if (!CanCloseDuringOperation()) { e.Cancel=true; return; } if (!_restoringDatabase && !_switchingUser && (!GoodsReceiptPanel.ConfirmClosing() || !ConfirmNoteChanges() || !ConfirmInvoiceChanges())) e.Cancel = true; };
#endif
            Closed += (s, e) => { _windowClosed = true; if (_backupTimer != null) _backupTimer.Stop(); if (_clockTimer != null) _clockTimer.Stop(); if(_receiptServer!=null)_receiptServer.Dispose(); StopScanner(); };
        }

        private bool CanCloseDuringOperation()
        {
            if (!_vm.IsBusy && !GoodsReceiptPanel.IsBusy && !_preparingRestore) return true;
            MessageBox.Show(this,"انتظر انتهاء العملية الحالية قبل الإغلاق أو تبديل الموظف.","عملية قيد التنفيذ");
            return false;
        }

        private bool ConfirmInvoiceChanges()
        {
            return (_vm.SaleLines.Count == 0 && _vm.PurchaseLines.Count == 0) ||
                MessageBox.Show(this,"يوجد سطور بيع أو شراء لم تُعتمد ولن تُحفظ عند الإغلاق. تجاهلها والمتابعة؟",
                    "فاتورة غير محفوظة",MessageBoxButton.YesNo,MessageBoxImage.Warning,
                    MessageBoxResult.No)==MessageBoxResult.Yes;
        }

        private void SwitchEmployeeClick(object sender, RoutedEventArgs e)
        {
            if (!CanCloseDuringOperation()) return;
            if (!GoodsReceiptPanel.ConfirmClosing() || !ConfirmNoteChanges() || !ConfirmInvoiceChanges()) return;
            RememberedLoginStore.Forget();
            var login = new UserLoginWindow(_service.Users) { Owner = this };
            if (login.ShowDialog() != true)
            {
                _switchingUser = true;
                Application.Current.Shutdown();
                return;
            }
            _service.SignIn(login.Session);
            var nextWindow = new MainWindow(_service);
            Application.Current.MainWindow = nextWindow;
            _switchingUser = true;
            Close();
            nextWindow.Show();
        }

        // Presentation-only: compact Windows displays keep the full working area
        // while wide screens show the extra blue brand rail.
        private void UpdateAdaptiveLayout(double width)
        {
            if (BrandColumn == null || BrandPanel == null) return;
            // Sidebar removed to maximize operational workspace
            BrandColumn.Width = new GridLength(0);
            BrandPanel.Visibility = Visibility.Collapsed;
        }

        private void UpdateClock()
        {
            if (ClockText == null) return;
            ClockText.Text = DateTime.Now.ToString(
                "dddd dd MMMM yyyy  •  hh:mm tt",
                CultureInfo.GetCultureInfo("ar-EG"));
        }

        private void StartReceiptSharing()
        {
            // No cloud or public web endpoint. If there is no private LAN,
            // normal sales and local printing continue without phone sharing.
            try { _receiptServer = OfflineReceiptServer.TryStart(_service); }
            catch (Exception) { _receiptServer = null; }
        }

        private void ShowSaleReceipt(string code)
        {
            try
            {
                var data = _service.ReadSaleReceipt(code);
                var url = _receiptServer == null ? null : _receiptServer.LinkForSale(code);
                new SaleReceiptWindow(data,url) { Owner=this }.ShowDialog();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "تم اعتماد فاتورة البيع، لكن تعذر فتح معاينتها. يمكنك الرجوع إلى قسم الفواتير.\n" + ex.Message,
                    "الفاتورة محفوظة",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private void OpenSelectedReceiptClick(object sender,RoutedEventArgs e)
        {
            if(!_service.Can("REPORTS")) return;
            var selected=_vm.SelectedDocument;
            if(selected == null || selected.Type != "SALE")
            {
                MessageBox.Show(this,"حدد فاتورة بيع من الجدول أولًا.","عرض الفاتورة",
                    MessageBoxButton.OK,MessageBoxImage.Information);
                return;
            }
            ShowSaleReceipt(selected.Code);
        }

        private void GoNotesClick(object sender, RoutedEventArgs e)
        {
            NotesTab.IsSelected = true;
            NoteTitleBox.Focus();
        }

        private void RefreshExternalBackupState()
        {
            var target = _service.ExternalBackupFolder;
            ExternalBackupPathText.Text = string.IsNullOrWhiteSpace(target)
                ? "غير مهيأ — اختر مجلدًا على USB أو قرص آخر غير قرص Windows."
                : target;
            if (string.IsNullOrWhiteSpace(target))
                BackupStatusText.Text = "تنبيه: لا توجد نسخ تلقائية حتى يتم تحديد وسيط خارجي.";
        }

        private void ConfigureBackupClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("BACKUP")) return;
            using (var dialog = new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description = "اختر قرص USB أو مجلدًا على قرص محلي غير قرص Windows";
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                try
                {
                    var target = _service.ConfigureExternalBackupFolder(dialog.SelectedPath);
                    RefreshExternalBackupState();
                    BackupStatusText.Text = "تم اختيار وسيط خارجي. يجري فحص إمكانية النسخ.";
                    BeginAutomaticBackup();
                    MessageBox.Show(this, "تم تحديد وجهة النسخ:\\n" + target +
                        "\\nالأفضل استخدام USB منفصل؛ قسم D: على نفس القرص لا يحمي من تلف القرص بالكامل.",
                        "إعداد النسخ الاحتياطي", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    BackupStatusText.Text = "تعذر إعداد النسخ: " + ex.Message;
                    MessageBox.Show(this, ex.Message, "تعذر إعداد النسخ",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }

        private void StartBackupScheduler()
        {
            RefreshExternalBackupState();
            _backupTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(20) };
            _backupTimer.Tick += (s, e) => BeginAutomaticBackup();
            _backupTimer.Start();
            BeginAutomaticBackup();
        }

        private void BeginAutomaticBackup()
        {
            if (_windowClosed || string.IsNullOrWhiteSpace(_service.ExternalBackupFolder)) return;
            if (Interlocked.CompareExchange(ref _backupRunning, 1, 0) != 0) return;
            BackupStatusText.Text = "جاري فحص النسخة الخارجية وإنشاء نسخة عند الحاجة...";
            Task.Run(() => _service.CreateScheduledExternalBackupIfDue())
                .ContinueWith(task =>
                {
                    Interlocked.Exchange(ref _backupRunning, 0);
                    if (_windowClosed) return;
                    if (task.IsFaulted)
                    {
                        BackupStatusText.Text = "تحذير: تعذر النسخ الخارجي — " +
                            task.Exception.GetBaseException().Message;
                        return;
                    }
                    BackupStatusText.Text = task.Result == null
                        ? "النسخة الخارجية الأخيرة ما زالت حديثة وتم التحقق منها."
                        : "تم إنشاء وفحص نسخة خارجية: " + task.Result;
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void ProductTableDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!_service.Can("CATALOG") || _vm.CatalogProduct == null) return;
            _vm.StartEditProductCommand.Execute(null);
            ProductNameInput.Focus();
            ProductNameInput.SelectAll();
        }
        private void GoGoodsReceiptClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("PURCHASE")) return;
            GoodsReceiptTab.IsSelected = true;
            GoodsReceiptPanel.FocusBarcode();
        }
        private void GoSalesClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("SALE")) return;
            SalesTab.IsSelected = true;
            SaleBarcodeInput.Focus();
        }
        private void GoProductsClick(object sender, RoutedEventArgs e)
        {
            ProductsTab.IsSelected = true;
            ProductNameInput.Focus();
        }
        private void GoPurchasesClick(object sender, RoutedEventArgs e)
        {
            if (_service.Can("PURCHASE")) PurchasesTab.IsSelected = true;
        }

        private async void MoveDataClick(object sender,RoutedEventArgs e)
        {
            if(!_service.Can("USERS") || !CanCloseDuringOperation())return;
            if(!GoodsReceiptPanel.ConfirmClosing() || !ConfirmNoteChanges() || !ConfirmInvoiceChanges())return;
            string folder;
            using(var dialog=new System.Windows.Forms.FolderBrowserDialog())
            {
                dialog.Description="اختر مجلدًا محليًا فارغًا ومخصصًا لبيانات البرنامج. احتفظ بالقرص متصلًا أثناء العمل.";
                if(dialog.ShowDialog()!=System.Windows.Forms.DialogResult.OK)return;folder=dialog.SelectedPath;
            }
            if(MessageBox.Show(this,"سيُغلق البرنامج لنقل نسخة مفحوصة عند التشغيل القادم. ستبقى البيانات الأصلية محفوظة، ولن يُستخدم مجلد قديم تلقائيًا إذا فُصل القرص. متابعة؟","نقل بيانات البرنامج",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
            _preparingRestore=true;IsEnabled=false;
            try
            {
                await Task.Run(()=>_service.StageDataRelocation(folder));
                _preparingRestore=false;_restoringDatabase=true;
                MessageBox.Show(this,"تم حفظ طلب النقل. افتح البرنامج مرة أخرى لاستكماله قبل تسجيل أي حركة جديدة.");
                Application.Current.Shutdown();
            }
            catch(Exception ex){MessageBox.Show(this,ex.Message,"تعذر نقل البيانات",MessageBoxButton.OK,MessageBoxImage.Warning);}
            finally {_preparingRestore=false;if(!_windowClosed)IsEnabled=true;}
        }
        private async void RestoreBackupClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("BACKUP") || !CanCloseDuringOperation()) return;
            var dialog = new OpenFileDialog
            {
                Title = "اختيار نسخة SQLite احتياطية لاسترجاعها",
                Filter = "SQLite database (*.db)|*.db",
                CheckFileExists = true,
                Multiselect = false
            };
            if (dialog.ShowDialog(this) != true) return;
            // Do not queue a database replacement if the employee cancels the
            // existing unsaved-notes / goods-receipt close confirmation.
            if (!GoodsReceiptPanel.ConfirmClosing() || !ConfirmNoteChanges() || !ConfirmInvoiceChanges()) return;
            if (MessageBox.Show(this,
                    "الاسترجاع سيستبدل قاعدة البيانات الحالية عند التشغيل القادم. " +
                    "سيتم حفظ نسخة أمان من البيانات الموجودة. " +
                    "تأكد أنك اخترت نسخة صحيحة، وأن جميع أعمال المحل محفوظة. هل تؤكد؟",
                    "تأكيد استرجاع قاعدة البيانات", MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            var filename=dialog.FileName;
            _preparingRestore=true; IsEnabled=false;
            try
            {
                await Task.Run(()=>_service.StageDatabaseRestore(filename));
                MessageBox.Show(this,
                    "تم التحقق من النسخة وجدولة الاسترجاع. " +
                    "سيُغلق البرنامج الآن. افتحه مرة أخرى لإكمال الاسترجاع.",
                    "النسخة جاهزة للاسترجاع", MessageBoxButton.OK,
                    MessageBoxImage.Information);
                _preparingRestore=false;
                _restoringDatabase = true;
                Application.Current.Shutdown();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "لم يتم جدولة الاسترجاع: " + ex.Message,
                    "خطأ الاسترجاع", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _preparingRestore=false; if(!_windowClosed) IsEnabled=true; }
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (_vm.IsBusy || GoodsReceiptPanel.IsBusy || _preparingRestore) { e.Handled=true; return; }
            if (e.Key == Key.F2 && GoodsReceiptTab.IsSelected && _service.Can("PURCHASE"))
            {
                GoodsReceiptPanel.FocusProductSearch();
                e.Handled = true;
            }
            else if (e.Key == Key.F2 && _service.Can("SALE"))
            {
                SalesTab.IsSelected = true;
                SaleBarcodeInput.Focus();
                SaleBarcodeInput.SelectAll();
                e.Handled = true;
            }
            else if (NotesTab.IsSelected && Keyboard.Modifiers == ModifierKeys.Control
                     && e.Key == Key.S)
            {
                SaveNoteClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (NotesTab.IsSelected && Keyboard.Modifiers == ModifierKeys.Control
                     && e.Key == Key.N)
            {
                NewNoteClick(this, new RoutedEventArgs());
                e.Handled = true;
            }
            else if (e.Key == Key.F5)
            {
                if (_vm.RefreshCommand.CanExecute(null)) _vm.RefreshCommand.Execute(null);
                e.Handled = true;
            }
        }
        private void SaleBarcodeKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            if (_vm.ScanSaleCommand.CanExecute(null)) _vm.ScanSaleCommand.Execute(null);
            SaleBarcodeInput.Focus();
            e.Handled = true;
        }

        private void StartScanner()
        {
            StopScanner();
            if (!_service.Can("SALE") && !_service.Can("PURCHASE"))
            {
                ScannerStatusText.Text = "ماسح COM: لا توجد صلاحية بيع أو توريد لهذا المستخدم";
                return;
            }
            try
            {
                var config = _service.LoadScannerSettings();
                if (!config.Enabled)
                {
                    ScannerStatusText.Text = "ماسح COM: غير مفعل";
                    return;
                }
                _scanner = new SerialBarcodeScanner(config);
                _scanner.BarcodeScanned += Scanner_BarcodeScanned;
                _scanner.DeviceError += Scanner_DeviceError;
                _scanner.Start();
                ScannerStatusText.Text = "ماسح COM: متصل بـ " + config.PortName;
            }
            catch (Exception ex)
            {
                StopScanner();
                ScannerStatusText.Text = "ماسح COM: تعذر الاتصال - " + ex.Message;
            }
        }
        private void Scanner_BarcodeScanned(string code)
        {
            // COM events are not raised on WPF's UI thread.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (_windowClosed || _vm.IsBusy || GoodsReceiptPanel.IsBusy || _preparingRestore) return;
                if (GoodsReceiptTab.IsSelected && _service.Can("PURCHASE"))
                {
                    GoodsReceiptPanel.AcceptBarcode(code);
                    ScannerStatusText.Text = "باركود التوريد: "+code;
                }
                else
                {
                    if (_vm.ApplyScannedBarcode(code)) SalesTab.IsSelected = true;
                    ScannerStatusText.Text = _vm.LastBarcodeResult;
                }
            }));
        }
        private void Scanner_DeviceError(string error)
        {
            Dispatcher.BeginInvoke(new Action(() => ScannerStatusText.Text = error));
        }
        private void StopScanner()
        {
            var old = _scanner;
            _scanner = null;
            if (old == null) return;
            old.BarcodeScanned -= Scanner_BarcodeScanned;
            old.DeviceError -= Scanner_DeviceError;
            try { old.Dispose(); }
            catch (Exception ex) { ScannerStatusText.Text = "تعذر إغلاق منفذ COM: " + ex.Message; }
        }
        private void ScannerSettingsClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("USERS")) { MessageBox.Show("إعدادات الأجهزة للمدير فقط."); return; }
            StopScanner();
            try { new SerialScannerSettingsWindow(_service) { Owner = this }.ShowDialog(); }
            finally { StartScanner(); }
        }
        private void FinanceClick(object sender,RoutedEventArgs e)
        {
            if(!_service.Can("USERS") || !CanCloseDuringOperation())return;
            new FinanceWindow(_service) {Owner=this}.ShowDialog();
            if(_vm.RefreshCommand.CanExecute(null))_vm.RefreshCommand.Execute(null);
        }
        private void SecurityClick(object sender, RoutedEventArgs e)
        {
            if (!_service.Can("USERS")) { MessageBox.Show("إدارة الموظفين للمدير فقط."); return; }
            new UserManagementWindow(_service) { Owner = this }.ShowDialog();
        }
    }
}
