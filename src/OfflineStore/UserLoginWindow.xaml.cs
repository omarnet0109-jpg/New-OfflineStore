using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OfflineStore.Security;

namespace OfflineStore
{
    public partial class UserLoginWindow : Window
    {
        private readonly UserAuthService _auth;
        private readonly bool _first;
        private bool _protectLegacy;
        private bool _syncingPassword;
        private bool _syncingConfirm;

        public UserSession Session { get; private set; }

        public UserLoginWindow(UserAuthService auth)
        {
            _auth = auth;
            InitializeComponent();
            _first = !auth.HasUsers();
            if (_first)
            {
                PageTitle.Text = "إنشاء مدير النظام — أول تشغيل";
                AdminNameLabel.Visibility = Visibility.Visible;
                AdminName.Visibility = Visibility.Visible;
                ConfirmLabel.Visibility = Visibility.Visible;
                ConfirmPasswordGrid.Visibility = Visibility.Visible;
                ConfirmPassword.Visibility = Visibility.Visible;
                RememberLoginBox.Visibility = Visibility.Collapsed;
                LoginName.IsReadOnly = true;
                SubmitButton.Content = "إنشاء المدير";
                PasswordLabel.Text = "الرقم السري للمدير (إلزامي)";
                ConfirmLabel.Text = "تأكيد الرقم السري";
                HelpText.Text = "أنشئ رقمًا سريًا من 6 إلى 12 رقمًا إنجليزيًا لحماية صلاحيات المدير. لا يمكن تركه فارغًا.";
            }
            Loaded += (s, e) =>
            {
                if (_first) AdminName.Focus();
                else LoginPassword.Focus();
            };
        }

        private void LoginPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || VisiblePassword == null) return;
            _syncingPassword = true;
            try { VisiblePassword.Text = LoginPassword.Password; }
            finally { _syncingPassword = false; }
        }

        private void VisiblePasswordChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingPassword || LoginPassword == null) return;
            _syncingPassword = true;
            try { LoginPassword.Password = VisiblePassword.Text; }
            finally { _syncingPassword = false; }
        }

        private void TogglePasswordClick(object sender, RoutedEventArgs e)
        {
            if (LoginPassword == null || VisiblePassword == null) return;
            bool show = LoginPassword.Visibility == Visibility.Visible;
            if (show)
            {
                LoginPassword.Visibility = Visibility.Collapsed;
                VisiblePassword.Visibility = Visibility.Visible;
                TogglePasswordButton.Content = "🔒 إخفاء";
                VisiblePassword.Focus();
                VisiblePassword.CaretIndex = VisiblePassword.Text.Length;
            }
            else
            {
                VisiblePassword.Visibility = Visibility.Collapsed;
                LoginPassword.Visibility = Visibility.Visible;
                TogglePasswordButton.Content = "👁 إظهار";
                LoginPassword.Focus();
            }
        }

        private void ConfirmPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingConfirm || VisibleConfirmPassword == null) return;
            _syncingConfirm = true;
            try { VisibleConfirmPassword.Text = ConfirmPassword.Password; }
            finally { _syncingConfirm = false; }
        }

        private void VisibleConfirmPasswordChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingConfirm || ConfirmPassword == null) return;
            _syncingConfirm = true;
            try { ConfirmPassword.Password = VisibleConfirmPassword.Text; }
            finally { _syncingConfirm = false; }
        }

        private void ToggleConfirmPasswordClick(object sender, RoutedEventArgs e)
        {
            if (ConfirmPassword == null || VisibleConfirmPassword == null) return;
            bool show = ConfirmPassword.Visibility == Visibility.Visible;
            if (show)
            {
                ConfirmPassword.Visibility = Visibility.Collapsed;
                VisibleConfirmPassword.Visibility = Visibility.Visible;
                ToggleConfirmPasswordButton.Content = "🔒 إخفاء";
                VisibleConfirmPassword.Focus();
                VisibleConfirmPassword.CaretIndex = VisibleConfirmPassword.Text.Length;
            }
            else
            {
                VisibleConfirmPassword.Visibility = Visibility.Collapsed;
                ConfirmPassword.Visibility = Visibility.Visible;
                ToggleConfirmPasswordButton.Content = "👁 إظهار";
                ConfirmPassword.Focus();
            }
        }

        private void Submit(object sender, RoutedEventArgs e)
        {
            try
            {
                ErrorMessage.Text = "";
                string password = (VisiblePassword != null && VisiblePassword.Visibility == Visibility.Visible)
                    ? VisiblePassword.Text : LoginPassword.Password;
                string confirmPassword = (VisibleConfirmPassword != null && VisibleConfirmPassword.Visibility == Visibility.Visible)
                    ? VisibleConfirmPassword.Text : ConfirmPassword.Password;

                if (_first)
                {
                    if (password != confirmPassword)
                        throw new ArgumentException("كلمتا المرور غير متطابقتين.");
                    _auth.CreateFirstAdmin(AdminName.Text, password);
                }
                if (_protectLegacy)
                {
                    if (password != confirmPassword)
                        throw new ArgumentException("الرقمان غير متطابقين.");
                    _auth.ProtectLegacyAdmin(LoginName.Text, password);
                }
                Session = _auth.Login(LoginName.Text, password);
                try
                {
                    if (RememberLoginBox.IsChecked == true && Session.Role != "ADMIN")
                        RememberedLoginStore.Save(Session.Username, password);
                    else
                        RememberedLoginStore.Forget();
                }
                catch (System.IO.IOException)
                {
                    // A failed opt-in save must never block a valid login.
                    MessageBox.Show(this, "تم تسجيل الدخول لكن تعذر حفظه على هذا الجهاز.",
                        "الدخول محفوظ للجلسة الحالية فقط", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    MessageBox.Show(this, "تم الدخول لكن تعذر تشفير حفظ الدخول لهذا حساب Windows.",
                        "الدخول محفوظ للجلسة الحالية فقط", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                LoginPassword.Clear(); ConfirmPassword.Clear();
                if (VisiblePassword != null) VisiblePassword.Clear();
                if (VisibleConfirmPassword != null) VisibleConfirmPassword.Clear();
                DialogResult = true;
            }
            catch (LegacyAdminPinRequiredException)
            {
                _protectLegacy = true;
                LoginName.IsReadOnly = true;
                ConfirmLabel.Visibility = ConfirmPassword.Visibility = Visibility.Visible;
                if (ConfirmPasswordGrid != null) ConfirmPasswordGrid.Visibility = Visibility.Visible;
                RememberLoginBox.IsChecked = false;
                RememberLoginBox.Visibility = Visibility.Collapsed;
                PageTitle.Text = "حماية حساب المدير القديم";
                PasswordLabel.Text = "PIN جديد (6 إلى 12 رقمًا إنجليزيًا)";
                SubmitButton.Content = "حفظ PIN وتسجيل الدخول";
                HelpText.Text = "الحساب القديم بلا كلمة مرور. عيّن PIN قبل فتح صلاحياته؛ لن تُحذف بياناته.";
                LoginPassword.Clear(); ConfirmPassword.Clear();
                if (VisiblePassword != null) VisiblePassword.Clear();
                if (VisibleConfirmPassword != null) VisibleConfirmPassword.Clear();
                LoginPassword.Focus();
            }
            catch (Exception ex)
            {
                LoginPassword.Clear(); ConfirmPassword.Clear();
                if (VisiblePassword != null) VisiblePassword.Clear();
                if (VisibleConfirmPassword != null) VisibleConfirmPassword.Clear();
                ErrorMessage.Text = ex.Message;
            }
        }

        private void PasswordEnter(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) Submit(sender, new RoutedEventArgs());
        }

        private void Cancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
