using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using OfflineStore.Security;
using OfflineStore.Services;

namespace OfflineStore
{
    public partial class UserManagementWindow : Window
    {
        private readonly StoreService _service;
        private bool _loadingPermissions;
        private bool _syncingPin;

        private string EnteredPin
        {
            get { return ShowPinBox.IsChecked == true ? VisiblePinInput.Text : PasswordInput.Password; }
        }

        private void PasswordInputChanged(object sender,RoutedEventArgs e)
        {
            if (_syncingPin || VisiblePinInput == null) return;
            _syncingPin = true;
            try { VisiblePinInput.Text = PasswordInput.Password; }
            finally { _syncingPin = false; }
        }

        private void VisiblePinChanged(object sender,TextChangedEventArgs e)
        {
            if (_syncingPin || PasswordInput == null) return;
            _syncingPin = true;
            try { PasswordInput.Password = VisiblePinInput.Text; }
            finally { _syncingPin = false; }
        }

        private void ShowPinChanged(object sender,RoutedEventArgs e)
        {
            if (PasswordInput == null || VisiblePinInput == null) return;
            var show = ShowPinBox.IsChecked == true;
            PasswordInput.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
            VisiblePinInput.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }
        private UserAuthService Auth { get { return _service.Users; } }

        public UserManagementWindow(StoreService service)
        {
            _service=service;
            InitializeComponent();
            Refresh();
            ApplyRolePreset();
        }

        private Dictionary<string, CheckBox> PermissionInputs()
        {
            return new Dictionary<string, CheckBox>(StringComparer.Ordinal)
            {
                {"SALE", AllowSale},
                {"PURCHASE", AllowPurchase},
                {"CATALOG", AllowCatalog},
                {"REPORTS", AllowReports},
                {"BACKUP", AllowBackup},
                {"LOW_STOCK", AllowLowStock},
                {"OVERRIDE_PRICE", AllowOverridePrice},
                {"APPEARANCE", AllowAppearance}
            };
        }

        private HashSet<string> CheckedPermissions()
        {
            var permissions=new HashSet<string>(StringComparer.Ordinal);
            foreach(var item in PermissionInputs())
                if(item.Value.IsChecked==true) permissions.Add(item.Key);
            return permissions;
        }

        private void ShowPermissions(IEnumerable<string> granted)
        {
            var allowed=new HashSet<string>(granted,StringComparer.Ordinal);
            _loadingPermissions=true;
            try
            {
                foreach(var item in PermissionInputs())
                    item.Value.IsChecked=allowed.Contains(item.Key);
            }
            finally { _loadingPermissions=false; }
        }

        private void ApplyRolePreset()
        {
            if(AllowSale==null || RoleInput==null) return;
            var role=RoleInput.SelectedValue as string;
            var allowed=new HashSet<string>(StringComparer.Ordinal);
            foreach(var permission in UserAuthService.ConfigurablePermissions)
                if(UserAuthService.RoleCan(role,permission)) allowed.Add(permission);
            ShowPermissions(allowed);
            PermissionsPanel.IsEnabled=role!="ADMIN";
            PermissionHeaderText.Text=role=="ADMIN"
                ? "مدير النظام: جميع الصلاحيات، ولا يمكن تعديلها من القائمة"
                : "صلاحيات الموظف الجديد — يمكنك تحديد المطلوب فقط";
        }

        private void RoleSelectionChanged(object sender,SelectionChangedEventArgs e)
        {
            if(_loadingPermissions || UsersGrid==null || UsersGrid.SelectedItem!=null) return;
            ApplyRolePreset();
        }

        private void UsersSelectionChanged(object sender,SelectionChangedEventArgs e)
        {
            if(_loadingPermissions || PermissionsPanel==null) return;
            var selected=UsersGrid.SelectedItem as UserInfo;
            if(selected==null)
            {
                ApplyRolePreset();
                return;
            }
            try
            {
                ShowPermissions(Auth.EffectivePermissions(_service.CurrentUser,selected.Id));
                PermissionsPanel.IsEnabled=selected.Role!="ADMIN";
                PermissionHeaderText.Text=selected.Role=="ADMIN"
                    ? "مدير النظام: صلاحيات كاملة غير قابلة للتخصيص"
                    : "تعديل صلاحيات: "+selected.DisplayName+" — اضغط حفظ صلاحيات المحدد بعد الاختيار";
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message,"تعذّر تحميل الصلاحيات",
                    MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private void Refresh()
        {
            var selected=UsersGrid.SelectedItem as UserInfo;
            var id=selected==null ? 0L : selected.Id;
            var users=Auth.Users(_service.CurrentUser);
            UsersGrid.ItemsSource=users;
            if(id!=0)
            {
                foreach(var user in users)
                    if(user.Id==id) { UsersGrid.SelectedItem=user;break; }
            }
        }

        private void Execute(Action op)
        {
            try
            {
                op();
                PasswordInput.Clear();
                Refresh();
            }
            catch(Exception ex)
            {
                MessageBox.Show(ex.Message,"تعذّر تنفيذ الإجراء",
                    MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }

        private UserInfo Selected()
        {
            var user=UsersGrid.SelectedItem as UserInfo;
            if(user==null) throw new ArgumentException("حدد الموظف من الجدول.");
            return user;
        }

        private void NewUserClick(object sender,RoutedEventArgs e)
        {
            UsersGrid.SelectedItem=null;
            UsernameInput.Clear();
            NameInput.Clear();
            PasswordInput.Clear();
            RoleInput.SelectedIndex=2;
            ApplyRolePreset();
            UsernameInput.Focus();
        }

        private void CreateUserClick(object sender,RoutedEventArgs e)
        {
            Execute(() =>
            {
                if(UsersGrid.SelectedItem!=null)
                    throw new InvalidOperationException(
                        "اضغط «موظف جديد» أولًا لإنشاء حساب مختلف عن الموظف المحدد.");
                var role=RoleInput.SelectedValue as string;
                Auth.CreateUserWithPermissions(_service.CurrentUser,
                    UsernameInput.Text,NameInput.Text,role,EnteredPin,
                    role=="ADMIN" ? null : CheckedPermissions());
                UsernameInput.Clear();
                NameInput.Clear();
                RoleInput.SelectedIndex=2;
                ApplyRolePreset();
            });
        }

        private void SavePermissionsClick(object sender,RoutedEventArgs e)
        {
            Execute(() =>
            {
                var selected=Selected();
                if(selected.Role=="ADMIN")
                    throw new InvalidOperationException("لا يمكن تقييد صلاحيات مدير النظام هنا.");
                if(MessageBox.Show(this,
                    "تأكيد تغيير صلاحيات "+selected.DisplayName+"؟ التعديل يسري على العمليات الجديدة فورًا.",
                    "حفظ صلاحيات الموظف",MessageBoxButton.YesNo,
                    MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)
                    return;
                Auth.SetUserPermissions(_service.CurrentUser,selected.Id,CheckedPermissions());
            });
        }

        private void ResetClick(object sender,RoutedEventArgs e)
        {
            Execute(() =>
            {
                var selected=Selected();
                var newPassword=EnteredPin;
                if(string.IsNullOrEmpty(newPassword))
                    throw new ArgumentException("اكتب رقم سر جديد من 6 إلى 12 رقمًا.");
                Auth.ResetPassword(_service.CurrentUser,selected.Id,newPassword);
                if (selected.Id == _service.CurrentUser.Id)
                    _service.SignIn(Auth.Login(selected.Username, newPassword));
            });
        }

        private void ToggleClick(object sender,RoutedEventArgs e)
        {
            Execute(() =>
            {
                var selected=Selected();
                Auth.SetActive(_service.CurrentUser,selected.Id,!selected.Active);
            });
        }
    }
}
