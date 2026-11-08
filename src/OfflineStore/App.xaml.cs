using System;
using System.Windows;
using OfflineStore.Appearance;
using OfflineStore.Security;
using OfflineStore.Services;
using System.Threading;
using System.Threading.Tasks;
using OfflineStore.Infrastructure;

namespace OfflineStore
{
    public partial class App : Application
    {
        private Mutex _singleInstance;
        private bool _ownsMutex;

        protected override void OnExit(ExitEventArgs e)
        {
            if (_ownsMutex && _singleInstance != null)
            {
                _singleInstance.ReleaseMutex();
                _ownsMutex = false;
            }
            if (_singleInstance != null) _singleInstance.Dispose();
            base.OnExit(e);
        }

        protected override async void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
#if SCREENSHOT_BUILD
            if (Array.IndexOf(e.Args, "--capture-preview") >= 0)
            {
                PreviewScreenshotCapture.Run();
                return;
            }
#endif
            try
            {
                _singleInstance = new Mutex(true,
                    @"Local\OfflineStore_" + Environment.UserName, out _ownsMutex);
                if (!_ownsMutex)
                {
                    MessageBox.Show("OfflineStore مفتوح بالفعل. أغلق النسخة الحالية أولًا.",
                        "البرنامج يعمل", MessageBoxButton.OK, MessageBoxImage.Information);
                    Shutdown();
                    return;
                }
                StoreService service=null;
                var location=new DatabaseLocationService();
                while(service==null)
                {
                    try {await Task.Run(()=>location.ApplyPendingMove());service=new StoreService();}
                    catch(DatabaseLocationUnavailableException locationError)
                    {
                        var message=locationError.Message+"\nنعم: إعادة المحاولة بعد توصيل القرص. لا: خيارات إصلاح مكان البيانات والاستعادة. إلغاء: إغلاق آمن.";
                        var choice=MessageBox.Show(message,"مكان البيانات غير متاح",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
                        if(choice==MessageBoxResult.Cancel){Shutdown(1);return;}
                        if(choice==MessageBoxResult.No)new DataLocationRecoveryWindow(location) {WindowStartupLocation=WindowStartupLocation.CenterScreen}.ShowDialog();
                    }
                }
                while (true)
                {
                    try { await Task.Run(() => service.Initialize()); break; }
                    catch (UpgradeBackupRequiredException backupError)
                    {
                        var action = MessageBox.Show(backupError.Message +
                            "\nنعم: إعادة المحاولة بعد التوصيل. لا: اختيار وسيط آخر بحساب المدير. إلغاء: إغلاق آمن.",
                            "وسيط النسخ غير متاح",MessageBoxButton.YesNoCancel,MessageBoxImage.Warning);
                        if (action == MessageBoxResult.Cancel) { Shutdown(1); return; }
                        if (action == MessageBoxResult.Yes) continue;
                        var login = new UserLoginWindow(service.Users);
                        if (login.ShowDialog() != true) { Shutdown(1); return; }
                        service.SignIn(login.Session);
                        if (!service.Can("USERS"))
                        { MessageBox.Show("تغيير وسيط النسخ قبل الترقية لمدير النظام فقط."); Shutdown(1); return; }
                        using (var folder = new System.Windows.Forms.FolderBrowserDialog())
                        {
                            folder.Description = "اختر USB أو قرصًا محليًا آخر لحفظ نسخة قبل الترقية";
                            if (folder.ShowDialog() != System.Windows.Forms.DialogResult.OK) continue;
                            try { service.ConfigureExternalBackupFolder(folder.SelectedPath); }
                            catch (Exception ex) { MessageBox.Show(ex.Message); }
                        }
                    }
                    catch (Exception initializationError)
                    {
                        var recovery = new StartupRecoveryWindow(service.DatabasePath, initializationError.Message);
                        if (recovery.ShowDialog() != true) { Shutdown(1); return; }
                        new SqliteRecoveryService(new StoreDb()).ApplyPendingRestoreAtStartup();
                    }
                }
                ThemeManager.Apply(service.Appearance.Load());
                // First-run administrator setup is handled by UserLoginWindow.
                // Never create a passwordless administrator on startup.
                // A saved employee token is encrypted by DPAPI for this Windows
                // account only. The full login/lockout check is repeated on startup.
                // ADMIN credentials are deliberately not remembered.
                {
                    var remembered = RememberedLoginStore.TryLoad();
                    if (remembered != null)
                    {
                        try
                        {
                            var session = service.Users.Login(remembered.Username, remembered.Password);
                            if (session.Role == "ADMIN")
                                RememberedLoginStore.Forget();
                            else
                                service.SignIn(session);
                        }
                        catch (Exception)
                        {
                            // Revoked PIN, suspended account or unreadable credentials:
                            // fall back to interactive login rather than bypassing it.
                            RememberedLoginStore.Forget();
                        }
                    }
                }
                if (service.CurrentUser == null)
                {
                    var login = new UserLoginWindow(service.Users);
                    if (login.ShowDialog() != true)
                    {
                        Shutdown();
                        return;
                    }
                    service.SignIn(login.Session);
                }
                var window = new MainWindow(service);
                MainWindow = window;
                ShutdownMode = ShutdownMode.OnMainWindowClose;
                window.Show();
            }
            catch (Exception ex)
            {
                MessageBox.Show("تعذر تشغيل البرنامج:\n" + ex.Message,
                    "خطأ في بدء التشغيل", MessageBoxButton.OK, MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }
}
