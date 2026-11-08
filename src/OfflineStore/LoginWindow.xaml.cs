using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OfflineStore.Security;

namespace OfflineStore
{
    public partial class LoginWindow : Window
    {
        private readonly LocalPasswordService _security;
        private int _failures;
        private bool _syncingPassword;

        public LoginWindow(LocalPasswordService security)
        {
            _security = security;
            InitializeComponent();
            Loaded += (s, e) => PasswordInput.Focus();
        }

        private void OnPasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_syncingPassword || VisiblePasswordInput == null) return;
            _syncingPassword = true;
            try { VisiblePasswordInput.Text = PasswordInput.Password; }
            finally { _syncingPassword = false; }
        }

        private void OnVisiblePasswordChanged(object sender, TextChangedEventArgs e)
        {
            if (_syncingPassword || PasswordInput == null) return;
            _syncingPassword = true;
            try { PasswordInput.Password = VisiblePasswordInput.Text; }
            finally { _syncingPassword = false; }
        }

        private void OnTogglePasswordClick(object sender, RoutedEventArgs e)
        {
            if (PasswordInput == null || VisiblePasswordInput == null) return;
            bool show = PasswordInput.Visibility == Visibility.Visible;
            if (show)
            {
                PasswordInput.Visibility = Visibility.Collapsed;
                VisiblePasswordInput.Visibility = Visibility.Visible;
                TogglePasswordButton.Content = "🔒 إخفاء";
                VisiblePasswordInput.Focus();
                VisiblePasswordInput.CaretIndex = VisiblePasswordInput.Text.Length;
            }
            else
            {
                VisiblePasswordInput.Visibility = Visibility.Collapsed;
                PasswordInput.Visibility = Visibility.Visible;
                TogglePasswordButton.Content = "👁 إظهار";
                PasswordInput.Focus();
            }
        }

        private void OnLoginClick(object sender, RoutedEventArgs e)
        {
            try
            {
                string pass = (VisiblePasswordInput != null && VisiblePasswordInput.Visibility == Visibility.Visible)
                    ? VisiblePasswordInput.Text : PasswordInput.Password;

                if (_security.Verify(pass))
                {
                    PasswordInput.Clear();
                    if (VisiblePasswordInput != null) VisiblePasswordInput.Clear();
                    DialogResult = true;
                    return;
                }
                _failures++;
                PasswordInput.Clear();
                if (VisiblePasswordInput != null) VisiblePasswordInput.Clear();
                ErrorMessage.Text = _failures >= 5
                    ? "تم رفض محاولات الدخول. أغلق النافذة وأعد المحاولة لاحقًا."
                    : "كلمة المرور غير صحيحة.";
                if (_failures >= 5) DialogResult = false;
                else PasswordInput.Focus();
            }
            catch (Exception ex)
            {
                ErrorMessage.Text = "تعذّر التحقق من كلمة المرور: " + ex.Message;
            }
        }

        private void OnCancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void OnPasswordKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) OnLoginClick(sender, e);
        }
    }
}
