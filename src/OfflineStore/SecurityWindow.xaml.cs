using System;
using System.Windows;
using OfflineStore.Security;

namespace OfflineStore
{
    public partial class SecurityWindow : Window
    {
        private readonly LocalPasswordService _security;
        public SecurityWindow(LocalPasswordService security)
        {
            _security = security;
            InitializeComponent();
            StatusText.Text = _security.IsEnabled() ? "كلمة المرور مفعّلة" : "كلمة المرور غير مفعّلة";
        }
        private void SaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                var disable = DisableProtection.IsChecked == true;
                if (!disable && NewPass.Password != ConfirmPass.Password)
                    throw new ArgumentException("تأكيد كلمة المرور لا يطابق الكلمة الجديدة.");
                _security.Change(OldPass.Password, NewPass.Password, disable);
                OldPass.Clear(); NewPass.Clear(); ConfirmPass.Clear();
                MessageBox.Show(disable ? "تم إلغاء كلمة المرور." : "تم حفظ كلمة المرور بنجاح.",
                    "الحماية", MessageBoxButton.OK, MessageBoxImage.Information);
                DialogResult = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "تعذّر حفظ الإعداد", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        private void CloseClick(object sender, RoutedEventArgs e) { DialogResult = false; }
    }
}
