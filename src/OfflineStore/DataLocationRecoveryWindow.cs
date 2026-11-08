using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using OfflineStore.Infrastructure;
using OfflineStore.Security;

namespace OfflineStore
{
    // Recovery choices are available before opening the working database. Never silently choose a retained source.
    internal sealed class DataLocationRecoveryWindow : Window
    {
        private readonly DatabaseLocationService _location;
        private readonly TextBlock _status;
        private readonly StackPanel _root;
        private bool _busy;
        internal DataLocationRecoveryWindow(DatabaseLocationService location)
        {
            _location=location;Title="إصلاح مكان بيانات البرنامج";Width=650;Height=440;FlowDirection=FlowDirection.RightToLeft;FontSize=15;
            _root=new StackPanel {Margin=new Thickness(22)};Content=new ScrollViewer {Content=_root,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            _root.Children.Add(new TextBlock {Text="لن يفتح البرنامج النسخة القديمة تلقائيًا. أعد توصيل القرص أو اختر إجراءً موثقًا أدناه. تبقى الملفات الأصلية محفوظة.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,15)});
            Add("إعادة ربط مجلد البيانات المعتمد بحساب مديره",Relink);
            var cancel=Add("إلغاء نقل لم تُعتمد وجهته بعد (بحساب مدير المصدر)",CancelMove);cancel.IsEnabled=location.CanCancelPendingMove;
            Add("استعادة نسخة احتياطية إلى مجلد بديل فارغ",Restore);
            _status=new TextBlock {TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,15,0,0)};_root.Children.Add(_status);
            Closing+=(s,e)=>{if(_busy)e.Cancel=true;};
        }
        private Button Add(string title,Func<Task> action)
        {
            var button=new Button {Content=title,Margin=new Thickness(0,6,0,6),MinHeight=42};
            button.Click+=async(s,e)=>{
                if(_busy)return;_busy=true;_root.IsEnabled=false;
                try{await action();}catch(Exception ex){_status.Text=ex.Message;}
                finally{_busy=false;_root.IsEnabled=true;}
            };
            _root.Children.Add(button);return button;
        }
        private static string Folder(string description)
        {
            using(var dialog=new System.Windows.Forms.FolderBrowserDialog())
            {dialog.Description=description;return dialog.ShowDialog()==System.Windows.Forms.DialogResult.OK?dialog.SelectedPath:null;}
        }
        private async Task Relink()
        {
            string folder=Folder("اختر مجلد البيانات المعتمد؛ ستُفحص هويته ولن يُنسخ ملف قديم فوقه.");if(folder==null)return;
            _status.Text="جاري فحص هوية المجلد والبيانات...";
            string candidate=await Task.Run(()=>_location.RelinkCandidate(folder));
            var login=new UserLoginWindow(new UserAuthService(new StoreDb(candidate))) {Owner=this};
            if(login.ShowDialog()!=true)return;
            await Task.Run(()=>_location.Relink(folder,login.Session));
            _busy=false;DialogResult=true;
        }
        private async Task CancelMove()
        {
            string source=await Task.Run(()=>_location.PendingSourcePath);
            var login=new UserLoginWindow(new UserAuthService(new StoreDb(source))) {Owner=this};
            if(login.ShowDialog()!=true)return;
            await Task.Run(()=>_location.CancelPendingMove(login.Session));
            _busy=false;DialogResult=true;
        }
        private async Task Restore()
        {
            string folder=Folder("اختر مجلدًا بديلًا فارغًا لاستعادة نسخة احتياطية. لن يُستبدل أي ملف موجود.");if(folder==null)return;
            string path=await Task.Run(()=>_location.PrepareAlternateRestoreFolder(folder));
            // This existing dialog verifies the administrator FROM THE BACKUP and requires explicit restore confirmation.
            string cookie=Path.Combine(Path.GetDirectoryName(path),"database-identity.txt"),preparedIdentity=File.ReadAllText(Path.Combine(Path.GetDirectoryName(path),"database-identity.txt"));
            var recovery=new StartupRecoveryWindow(path,"مكان البيانات السابق غير متاح. الاستعادة إلى المجلد البديل المختار.") {Owner=this};
            if(recovery.ShowDialog()!=true)
            {
                // Remove only our unused reservation; preserve every partial restore/other file for recovery.
                var entries=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)).ToArray();
                if(entries.Length==1 && File.Exists(cookie) && File.ReadAllText(cookie)==preparedIdentity)File.Delete(cookie);
                return;
            }
            await Task.Run(()=>_location.AdoptAuthenticatedRestore(path));
            MessageBox.Show(this,"تمت الاستعادة إلى المجلد البديل. راجع إعدادات وسيط النسخ الخارجي بعد الدخول.");
            _busy=false;DialogResult=true;
        }
    }
}
