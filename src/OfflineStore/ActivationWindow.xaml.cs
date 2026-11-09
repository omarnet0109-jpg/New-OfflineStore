using System;
using System.Windows;
using OfflineStore.Security;

namespace OfflineStore
{
    public partial class ActivationWindow : Window
    {
        public ActivationWindow()
        {
            InitializeComponent();
            Loaded += (s, e) => Part1.Focus();
        }

        private void OnActivate(object sender, RoutedEventArgs e)
        {
            try
            {
                var key = string.Join("-", Part1.Text, Part2.Text, Part3.Text, Part4.Text);
                if (!ActivationService.Activate(key))
                {
                    ErrorText.Text = "السيريال نمبر غير صحيح.";
                    return;
                }
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ErrorText.Text = "تعذر حفظ التفعيل: " + ex.Message;
            }
        }
    }
}
