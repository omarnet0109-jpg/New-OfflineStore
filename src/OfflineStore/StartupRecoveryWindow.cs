using System;
using System.Windows;
using System.Windows.Controls;
using System.Threading.Tasks;
using Microsoft.Win32;
using OfflineStore.Infrastructure;

namespace OfflineStore
{
    // Available before database initialization; authentication reads the selected backup only.
    public sealed class StartupRecoveryWindow : Window
    {
        public StartupRecoveryWindow(string databasePath, string failure)
        {
            Title="استعادة البيانات عند تعذر التشغيل"; Width=570; Height=470;
            WindowStartupLocation=WindowStartupLocation.CenterScreen;
            FlowDirection=FlowDirection.RightToLeft;
            var root=new StackPanel { Margin=new Thickness(20) };
            root.Children.Add(new TextBlock { Text="تعذر فتح البيانات: "+failure,
                TextWrapping=TextWrapping.Wrap, MaxHeight=110 });
            root.Children.Add(new TextBlock { Text="اختر نسخة مستقلة، ثم أدخل حساب مدير موجود فيها. ستُحفظ الملفات القديمة مع WAL وSHM.", TextWrapping=TextWrapping.Wrap });
            var choose=new Button { Content="اختيار نسخة احتياطية" }; root.Children.Add(choose);
            var file=new TextBlock { TextWrapping=TextWrapping.Wrap }; root.Children.Add(file);
            var name=new TextBox { Text="admin" }; root.Children.Add(name);
            var pin=new PasswordBox(); root.Children.Add(pin);
            root.Children.Add(new TextBlock { Text="النسخ القديمة بلا PIN تتطلب امتياز مدير Windows. لا يُقبل حساب موظف للاستعادة.", TextWrapping=TextWrapping.Wrap });
            var status=new TextBlock { TextWrapping=TextWrapping.Wrap }; root.Children.Add(status);
            var apply=new Button { Content="تأكيد الاستعادة مع الاحتفاظ بالأصل" }; root.Children.Add(apply);
            string selected=null; int attempts=0;
            choose.Click+=(s,e)=> {
                var dialog=new OpenFileDialog { Filter="SQLite backup (*.db)|*.db",CheckFileExists=true };
                if(dialog.ShowDialog(this)==true) { selected=dialog.FileName; file.Text=selected; }
            };
            bool working=false;
            Closing+=(s,e)=> {if(working)e.Cancel=true;};
            apply.Click+=async (s,e)=> {
                if(working || attempts>=5)return;
                if(selected==null) { status.Text="اختر النسخة أولًا."; return; }
                var backup=selected; var username=name.Text.Trim(); var password=pin.Password;
                working=true; apply.IsEnabled=choose.IsEnabled=name.IsEnabled=pin.IsEnabled=false;
                Cursor=System.Windows.Input.Cursors.Wait;
                bool restored=false;
                try
                {
                    status.Text="جاري فحص النسخة وصلاحية المدير...";
                    var recovery=new SqliteRecoveryService(new StoreDb(databasePath));
                    bool authorized=await Task.Run(()=>recovery.VerifyRecoveryAdministrator(backup,username,password));
                    if(!authorized)
                    {
                        attempts++; pin.Clear(); status.Text="بيانات مدير النسخة غير صحيحة أو الحساب مغلق.";
                        return;
                    }
                    if(MessageBox.Show(this,"استبدال البيانات الحالية بالنسخة المختارة؟ ستبقى نسخة خام من الملفات الأصلية.",
                        Title,MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes) return;
                    status.Text="جاري حفظ الأصل واستعادة النسخة...";
                    await Task.Run(()=> {
                        recovery.StageRestore(backup, true);
                        recovery.ApplyPendingRestoreAtStartup();
                    });
                    restored=true;
                }
                catch(Exception ex) { status.Text=ex.Message; }
                finally
                {
                    working=false; choose.IsEnabled=name.IsEnabled=pin.IsEnabled=true;
                    apply.IsEnabled=attempts<5; Cursor=null;
                }
                if(restored)DialogResult=true;
            };
            Content=new ScrollViewer { Content=root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        }
    }
}
