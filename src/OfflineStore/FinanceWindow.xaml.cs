using System;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.Windows.Documents;
using System.Windows.Media;
using System.Data;
using System.Data.SQLite;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using OfflineStore.Infrastructure;
using OfflineStore.Models;
using OfflineStore.Services;

namespace OfflineStore
{
    public sealed class FinanceMoneyConverter : IValueConverter
    {
        public object Convert(object value,Type target,object parameter,CultureInfo culture)
        {return value==null || value==DBNull.Value?"":Amounts.Price(System.Convert.ToInt64(value,CultureInfo.InvariantCulture));}
        public object ConvertBack(object value,Type target,object parameter,CultureInfo culture) {throw new NotSupportedException();}
    }
    public sealed class OpeningLineInput
    {
        public string AccountCode {get;set;}
        public long? PartyId {get;set;}
        public string Debit {get;set;}="0";
        public string Credit {get;set;}="0";
    }
    public partial class FinanceWindow : Window
    {
        private readonly StoreService _store;
        private readonly FinanceService _finance;
        private bool _busy;
        private bool _openingLoaded;
        private bool _accountingActive;
        private string _openingBaseline;
        private OpeningReview _openingReview;
        private string _legacyResultsSummary;
        private long _inventoryBook,_inventoryValue;
        private List<ReturnableLine> _returnLines=new List<ReturnableLine>();
        private long _returnDocument;
        private string _lastReturn;
        public DataView Accounts {get;private set;}
        public DataView Parties {get;private set;}
        public ObservableCollection<OpeningLineInput> OpeningLines {get;}=new ObservableCollection<OpeningLineInput>();
        public FinanceWindow(StoreService store)
        {
            _store=store;_finance=store.Finance;InitializeComponent();DataContext=this;
            FromDate.SelectedDate=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
            ToDate.SelectedDate=OperationDate.SelectedDate=DateTime.Today;
            Reference.Text=Guid.NewGuid().ToString("N");
            AllocationReference.Text=Guid.NewGuid().ToString("N");
            ReturnReference.Text=Guid.NewGuid().ToString("N");
            OpeningLines.Add(new OpeningLineInput {AccountCode="CASH"});
            OpeningLines.Add(new OpeningLineInput {AccountCode="OPENING_EQUITY"});
            Loaded+=async(s,e)=>await Run(Refresh);
            Closing+=(s,e)=>{if(_busy || !ConfirmPendingFinanceChanges())e.Cancel=true;};
        }
        private string OpeningSignature()
        {return string.Join("|",OpeningLines.Select(x=>x.AccountCode+":"+x.PartyId+":"+x.Debit+":"+x.Credit));}
        private static bool NonzeroInput(string text)
        {
            if(string.IsNullOrWhiteSpace(text))return false;
            try{return Amounts.ToMilli(text,"المدخل",true)!=0;}catch(ArgumentException){return true;}catch(OverflowException){return true;}
        }
        private bool ConfirmPendingFinanceChanges()
        {
            OpeningGrid.CommitEdit(DataGridEditingUnit.Cell,true);OpeningGrid.CommitEdit(DataGridEditingUnit.Row,true);
            ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Cell,true);ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Row,true);
            bool dirty=NonzeroInput(Amount.Text) || !string.IsNullOrWhiteSpace(Memo.Text) || NonzeroInput(AllocationAmount.Text)
                || _returnLines.Any(x=>NonzeroInput(x.Quantity)) || NonzeroInput(ReturnDebt.Text) || NonzeroInput(ReturnCredit.Text)
                || !string.IsNullOrWhiteSpace(ReturnMemo.Text)
                || (!_accountingActive && _openingBaseline!=null && OpeningSignature()!=_openingBaseline);
            return !dirty || MessageBox.Show(this,"توجد مدخلات مالية أو مرتجع أو افتتاح لم تُعتمد. تجاهلها وإغلاق الشاشة؟","مدخلات مالية غير معتمدة",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)==MessageBoxResult.Yes;
        }
        private async Task Run(Func<Task> action)
        {
            if(_busy)return;_busy=true;IsEnabled=false;
            try {await action();}catch(Exception ex){
                string message=ex.Message;
                if(ex is SQLiteException)
                {
                    if(message.IndexOf("UNIQUE",StringComparison.OrdinalIgnoreCase)>=0)message="هذا المرجع مسجل بالفعل. راجع السجل قبل أي إعادة محاولة بمرجع جديد.";
                    else if(message.IndexOf("locked",StringComparison.OrdinalIgnoreCase)>=0 || message.IndexOf("busy",StringComparison.OrdinalIgnoreCase)>=0)message="البيانات مشغولة بعملية أخرى. انتظر ثم حدّث السجل وتحقق من نتيجة الحركة قبل تكرارها.";
                }
                MessageBox.Show(this,message,"الحسابات",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            finally {_busy=false;IsEnabled=true;}
        }
        private async Task Refresh()
        {
            DateTime from=FromDate.SelectedDate??DateTime.Today,to=ToDate.SelectedDate??DateTime.Today;
            var data=await Task.Run(()=>{var review=_finance.CaptureOpeningReview();return new {Review=review,Balances=_finance.PartyBalances(),Accounts=_finance.Accounts(),Trial=_finance.TrialBalance(to),Profit=_finance.ProfitAndLoss(from,to),Active=_finance.IsActive,Invoices=_finance.FinancialInvoices(),Unallocated=_finance.UnallocatedSettlements(),StockValue=_finance.OpeningInventoryValueCents(),InventoryBook=_finance.InventoryBookCents(),InventoryReview=_finance.InventoryReviewRequired,Cash=_finance.CashBalanceCents(),Opening=_finance.ReviewedOpeningLines(),LegacyResults=_finance.LegacyResults(),Returns=_store.Returns.List(from,to)};});
            _openingReview=data.Review;_inventoryBook=data.InventoryBook;_inventoryValue=data.StockValue;
            InventoryReviewButton.Visibility=data.InventoryReview?Visibility.Visible:Visibility.Collapsed;
            _legacyResultsSummary=data.Active?"":string.Join("، ",data.LegacyResults.Rows.Cast<DataRow>().Select(row=>(string)row["account_code"]+": "+Amounts.Price(System.Convert.ToInt64(row["net"]))));
            object selectedReturn=ReturnInvoice.SelectedValue,selectedCreditParty=ReturnCreditParty.SelectedValue;
            Accounts=data.Accounts.DefaultView;Parties=data.Balances.DefaultView;
            if(!_openingLoaded && data.Opening.Rows.Count>0)
            {
                OpeningLines.Clear();
                foreach(DataRow row in data.Opening.Rows)
                {
                    long net=System.Convert.ToInt64(row["net"]);
                    OpeningLines.Add(new OpeningLineInput {AccountCode=(string)row["account_code"],PartyId=row.IsNull("party_id")?(long?)null:System.Convert.ToInt64(row["party_id"]),Debit=Amounts.EditablePrice(net>0?net:0),Credit=Amounts.EditablePrice(net<0?checked(-net):0)});
                }
            }
            if(!_openingLoaded)_openingBaseline=OpeningSignature();
            _openingLoaded=true;_accountingActive=data.Active;
            Balances.ItemsSource=Parties;OperationParty.ItemsSource=Parties;ReturnCreditParty.ItemsSource=Parties;
            var expenses=new DataView(data.Accounts) {RowFilter="kind='EXPENSE' AND code NOT IN ('COGS','INVENTORY_VARIANCE')"};ExpenseAccount.ItemsSource=expenses;
            Trial.ItemsSource=data.Trial.DefaultView;
            ReturnHistory.ItemsSource=data.Returns.DefaultView;
            ReturnInvoice.ItemsSource=data.Invoices.DefaultView;ReturnInvoice.SelectedValue=selectedReturn;ReturnCreditParty.SelectedValue=selectedCreditParty;
            FinancialInvoiceGrid.ItemsSource=AllocationInvoice.ItemsSource=data.Invoices.DefaultView;
            AllocationPayment.ItemsSource=data.Unallocated.DefaultView;
            Profit.Text="صافي المبيعات: "+Amounts.Price(data.Profit.SalesCents)+" — فروق مرتجعات وإيرادات أخرى: "+Amounts.Price(data.Profit.OtherIncomeCents)+" — تكلفة المبيعات: "+Amounts.Price(data.Profit.CostOfSalesCents)+" — مصروفات: "+Amounts.Price(data.Profit.ExpensesCents)+" — صافي: "+Amounts.Price(data.Profit.NetCents);
            OpeningInventoryHint.Text="قيمة المخزون بمتوسطاته المسجلة: "+Amounts.Price(data.StockValue)+" جنيه. أدخلها مدينًا في سطر المخزون عند الافتتاح؛ راجع صحة التكلفة قبل التفعيل.";
            Status.Text=data.Active?"الحسابات مفعلة؛ الفواتير الجديدة مرتبطة بالأموال والمخزون.":"الحسابات غير مفعلة. راجع أرصدة الافتتاح أولًا. لا تُسجل الفواتير في الأموال حتى التفعيل.";
            Status.Text+=" — رصيد الخزنة: "+Amounts.Price(data.Cash)+" جنيه";
            DataContext=null;DataContext=this;
        }
        private async void RefreshClick(object s,RoutedEventArgs e){await Run(Refresh);}
        private async void CreatePartyClick(object s,RoutedEventArgs e)
        {
            string type=(string)((ComboBoxItem)PartyType.SelectedItem).Tag,name=PartyName.Text,phone=Phone.Text;
            await Run(async()=>{await Task.Run(()=>_finance.CreateParty(type,name,phone));PartyName.Clear();Phone.Clear();await Refresh();});
        }
        private async void CreateCategoryClick(object s,RoutedEventArgs e)
        {string name=CategoryName.Text;await Run(async()=>{await Task.Run(()=>_finance.CreateExpenseCategory(name));CategoryName.Clear();await Refresh();});}
        private async void PostClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                string type=(string)((ComboBoxItem)OperationType.SelectedItem).Tag;
                long amount=Amounts.ToCents(Amount.Text,"المبلغ");
                long? party=OperationParty.SelectedValue==null?(long?)null:System.Convert.ToInt64(OperationParty.SelectedValue);
                string category=ExpenseAccount.SelectedValue as string,reference=Reference.Text,memo=Memo.Text;
                DateTime date=OperationDate.SelectedDate??DateTime.Today;
                string effect=type.EndsWith("_CREDIT",StringComparison.Ordinal)?"تطبيق رصيد دائن من مرتجع؛ لا يتحرك النقد ولا الربح.":type.EndsWith("_ADVANCE",StringComparison.Ordinal)?"تطبيق مقدم نقدي سابق؛ لا يتحرك النقد ولا الربح.":type=="CAPITAL"?"تمويل من رأس المال وليس إيرادًا.":"حركة نقدية؛ زيادة التحصيل أو السداد عن الدين تسجل مقدمًا مستقلًا.";
                if(MessageBox.Show(this,"اعتماد حركة بمبلغ "+Amounts.Price(amount)+" جنيه؟\n"+effect,"تأكيد حركة الأموال",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                long id=await Task.Run(()=>type=="CAPITAL"?_finance.FundCash(amount,reference,date,memo):type=="EXPENSE"?_finance.RecordExpense(category,amount,reference,date,memo):
                    type=="CUSTOMER_CREDIT"?_finance.ApplyReturnCredit(true,party??0,amount,reference,date,memo):
                    type=="SUPPLIER_CREDIT"?_finance.ApplyReturnCredit(false,party??0,amount,reference,date,memo):
                    type=="CUSTOMER_ADVANCE"?_finance.ApplyAdvance(true,party??0,amount,reference,date,memo):
                    type=="SUPPLIER_ADVANCE"?_finance.ApplyAdvance(false,party??0,amount,reference,date,memo):
                    type=="CUSTOMER_PAYMENT"?_finance.Collect(party??0,amount,reference,date,memo):_finance.Pay(party??0,amount,reference,date,memo));
                Reference.Text=Guid.NewGuid().ToString("N");Amount.Text="0";Memo.Clear();await Refresh();
                MessageBox.Show(this,"تم اعتماد القيد رقم "+id,"حركة محفوظة");
            });
        }
        private async void AllocateClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                if(AllocationPayment.SelectedValue==null || AllocationInvoice.SelectedValue==null)throw new ArgumentException("اختر حركة السداد والفاتورة.");
                long entry=System.Convert.ToInt64(AllocationPayment.SelectedValue),doc=System.Convert.ToInt64(AllocationInvoice.SelectedValue);
                long amount=Amounts.ToCents(AllocationAmount.Text,"مبلغ التوزيع");string reference=AllocationReference.Text;
                if(MessageBox.Show(this,"ربط هذا المبلغ بالفاتورة المحددة؟ التوزيع المعتمد لا يُعدل.","تأكيد توزيع السداد",MessageBoxButton.YesNo,MessageBoxImage.Question,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                await Task.Run(()=>_finance.AllocateSettlement(entry,doc,amount,reference));
                AllocationReference.Text=Guid.NewGuid().ToString("N");AllocationAmount.Text="0";await Refresh();
            });
        }
        private async void LoadReturnClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                if(ReturnInvoice.SelectedValue==null)throw new ArgumentException("اختر الفاتورة الأصلية.");
                long doc=System.Convert.ToInt64(ReturnInvoice.SelectedValue);
                var rows=await Task.Run(()=>_store.Returns.Lines(doc));
                if(rows.Count==0)throw new ArgumentException("لا توجد سطور مالية موثقة قابلة للإرجاع.");
                _returnDocument=doc;var selected=ReturnInvoice.SelectedItem as DataRowView;ReturnCreditParty.SelectedValue=selected==null || selected.Row.IsNull("party_id")?null:selected["party_id"];_returnLines=rows;ReturnLinesGrid.ItemsSource=rows;ReturnSummary.Text="";
            });
        }
        private List<ReturnQuantity> ReturnQuantities()
        {
            ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Cell,true);ReturnLinesGrid.CommitEdit(DataGridEditingUnit.Row,true);
            if(ReturnInvoice.SelectedValue==null || System.Convert.ToInt64(ReturnInvoice.SelectedValue)!=_returnDocument)throw new ArgumentException("حمّل سطور الفاتورة المحددة أولًا.");
            return _returnLines.Select(x=>new ReturnQuantity {OriginalLineId=x.OriginalLineId,QuantityMilli=Amounts.ToMilli(x.Quantity,x.Name,true)}).Where(x=>x.QuantityMilli>0).ToList();
        }
        private static string ReturnText(ReturnQuote q)
        {
            return "الأصل: "+q.OriginalCode+" — قيمة المرتجع: "+Amounts.Price(q.TotalCents)+" — نقد: "+Amounts.Price(q.CashCents)+" — تخفيض الدين: "+Amounts.Price(q.DebtCents)+" — رصيد دائن: "+Amounts.Price(q.CreditCents)+" — تكلفة حركة المخزون: "+Amounts.Price(q.CostCents)+(q.Purchase?" — فرق تكلفة المشتريات: "+Amounts.Price(checked(q.TotalCents-q.CostCents)):"");
        }
        private async void PreviewReturnClick(object s,RoutedEventArgs e)
        {await Run(async()=>{var lines=ReturnQuantities();long debt=Amounts.ToCents(ReturnDebt.Text,"تخفيض الدين");long credit=Amounts.ToCents(ReturnCredit.Text,"الرصيد الدائن");long? beneficiary=ReturnCreditParty.SelectedValue==null?(long?)null:System.Convert.ToInt64(ReturnCreditParty.SelectedValue);var q=await Task.Run(()=>_store.Returns.Preview(_returnDocument,lines,debt,credit,beneficiary));ReturnSummary.Text=ReturnText(q);});}
        private async void PostReturnClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                var lines=ReturnQuantities();long debt=Amounts.ToCents(ReturnDebt.Text,"تخفيض الدين"),doc=_returnDocument;
                long credit=Amounts.ToCents(ReturnCredit.Text,"الرصيد الدائن");long? beneficiary=ReturnCreditParty.SelectedValue==null?(long?)null:System.Convert.ToInt64(ReturnCreditParty.SelectedValue);
                string reference=ReturnReference.Text,memo=ReturnMemo.Text;
                var quote=await Task.Run(()=>_store.Returns.Preview(doc,lines,debt,credit,beneficiary));ReturnSummary.Text=ReturnText(quote);
                if(MessageBox.Show(this,ReturnSummary.Text+"\nاعتماد حركة المخزون والأموال معًا؟ لا يمكن تعديل المرتجع المعتمد.","تأكيد المرتجع",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                _lastReturn=await Task.Run(()=>_store.Returns.Post(doc,lines,debt,reference,memo,credit,beneficiary));
                ReturnReference.Text=Guid.NewGuid().ToString("N");ReturnMemo.Clear();ReturnDebt.Text="0";ReturnCredit.Text="0";
                ReturnLinesGrid.ItemsSource=null;_returnLines.Clear();_returnDocument=0;
                await Refresh();ReturnSummary.Text="تم اعتماد المرتجع "+_lastReturn+". يمكنك عرض إيصال المرتجع وطباعته.";
            });
        }
        private void PrintSelectedReturnClick(object s,RoutedEventArgs e)
        {
            var row=ReturnHistory.SelectedItem as DataRowView;
            if(row==null){MessageBox.Show(this,"حدد المرتجع من السجل أولًا.");return;}
            _lastReturn=System.Convert.ToString(row["code"]);PrintReturnClick(s,e);
        }
        private async void PrintReturnClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                if(_lastReturn==null)throw new ArgumentException("اعتمد مرتجعًا أولًا.");
                var receipt=await Task.Run(()=>_store.Returns.Receipt(_lastReturn));var first=receipt.Rows[0];
                var document=new FlowDocument {FlowDirection=FlowDirection.RightToLeft,FontFamily=new FontFamily("Segoe UI"),FontSize=14,PagePadding=new Thickness(14)};
                document.Blocks.Add(new Paragraph(new Run("إيصال مرتجع "+_lastReturn)) {FontSize=20,FontWeight=FontWeights.Bold});
                document.Blocks.Add(new Paragraph(new Run("الفاتورة الأصلية: "+first["doc_code"]+" — "+first["return_date"])));
                foreach(DataRow row in receipt.Rows)document.Blocks.Add(new Paragraph(new Run(row["name_snapshot"]+" — "+Amounts.Qty(System.Convert.ToInt64(row["qty_milli"]))+" "+row["unit_snapshot"]+" — "+Amounts.Price(System.Convert.ToInt64(row["amount_cents"]))+" جنيه")));
                document.Blocks.Add(new Paragraph(new Run("الإجمالي: "+Amounts.Price(System.Convert.ToInt64(first["total_cents"]))+" — نقد: "+Amounts.Price(System.Convert.ToInt64(first["cash_cents"]))+" — تخفيض الدين: "+Amounts.Price(System.Convert.ToInt64(first["debt_cents"]))))) ;
                document.Blocks.Add(new Paragraph(new Run("رصيد دائن: "+Amounts.Price(System.Convert.ToInt64(first["credit_cents"]))+" — المستفيد: "+first["credit_party_name"])));
                document.Blocks.Add(new Paragraph(new Run(System.Convert.ToString(first["memo"]))));
                var viewer=new FlowDocumentScrollViewer {Document=document};var panel=new DockPanel();
                var print=new Button {Content="اختيار الطابعة والورق ثم الطباعة",Margin=new Thickness(8)};
                print.Click+=(sender,args)=>{
                    try
                    {
                        var dialog=new PrintDialog();if(dialog.ShowDialog()!=true)return;
                        var oldWidth=document.PageWidth;var oldHeight=document.PageHeight;
                        try
                        {
                            document.PageWidth=dialog.PrintableAreaWidth;document.PageHeight=dialog.PrintableAreaHeight;
                            document.ColumnWidth=Math.Max(1,dialog.PrintableAreaWidth-28);
                            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator,"مرتجع "+_lastReturn);
                        }
                        finally {document.PageWidth=oldWidth;document.PageHeight=oldHeight;document.ColumnWidth=double.NaN;}
                    }
                    catch(Exception ex){MessageBox.Show(ex.Message,"تعذر طباعة المرتجع");}
                };
                DockPanel.SetDock(print,Dock.Top);panel.Children.Add(print);panel.Children.Add(viewer);
                new Window {Owner=this,Title="إيصال المرتجع",Content=panel,Width=750,Height=650,WindowStartupLocation=WindowStartupLocation.CenterOwner,FlowDirection=FlowDirection.RightToLeft}.ShowDialog();
            });
        }
        private async void InventoryReviewClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                if(MessageBox.Show(this,"القيمة المنقولة من سياسة التقييم السابقة: "+Amounts.Price(_inventoryValue)+" جنيه؛ رصيد حساب المخزون: "+Amounts.Price(_inventoryBook)+" جنيه.\nراجع الأصناف والتكاليف أولًا. اعتماد الفرق يسجل قيد فروق مخزون سابق بتاريخ اليوم، ولا يعدّل أي فاتورة أو قيد قديم. هل تعتمد التسوية؟","مراجعة فرق مخزون قديم",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                await Task.Run(()=>_finance.ReviewLegacyInventory(_openingReview));await Refresh();
            });
        }
        private void AddOpeningLineClick(object s,RoutedEventArgs e){OpeningLines.Add(new OpeningLineInput());}
        private async void OpeningClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                OpeningGrid.CommitEdit(DataGridEditingUnit.Cell,true);OpeningGrid.CommitEdit(DataGridEditingUnit.Row,true);
                var lines=OpeningLines.Where(x=>!string.IsNullOrWhiteSpace(x.AccountCode)).Select(x=>new JournalLine {AccountCode=x.AccountCode,PartyId=x.PartyId,DebitCents=Amounts.ToCents(x.Debit,"مدين"),CreditCents=Amounts.ToCents(x.Credit,"دائن")}).Where(x=>x.DebitCents!=0 || x.CreditCents!=0).ToList();
                bool closeResults=!string.IsNullOrEmpty(_legacyResultsSummary);
                string confirmation="هل راجعت الأرصدة الفعلية لكل حساب وطرف؟ سيُحفظ الافتتاح والتفعيل معًا. إذا وُجد دفتر سابق، تُحفظ تسوية بالفرق للوصول للأرصدة التي أدخلتها دون تعديل القيود السابقة.";
                if(closeResults)confirmation+="\nنتائج سابقة (مدين موجب / دائن سالب): "+_legacyResultsSummary+"\nبالاعتماد توافق على إقفال هذه النتائج في حقوق الملكية ضمن الأرصدة المراجعة. المقترح يجمعها في OPENING_EQUITY؛ راجع المقابل. لن تدخل في أرباح الفترة الجديدة، وتظل القيود القديمة محفوظة.";
                if(MessageBox.Show(this,confirmation,"مراجعة الرصيد الافتتاحي",MessageBoxButton.YesNo,MessageBoxImage.Warning,MessageBoxResult.No)!=MessageBoxResult.Yes)return;
                var id=await Task.Run(()=>_finance.Activate(lines,_openingReview,closeResults));
                _accountingActive=true;await Refresh();MessageBox.Show(this,"تم تفعيل الحسابات وتثبيت الأرصدة المراجعة في معاملة واحدة. "+(id.HasValue?"رقم الافتتاح: "+id.Value:"دون إنشاء قيد افتتاح إضافي."));
            });
        }
        private async void StatementClick(object s,RoutedEventArgs e)
        {
            await Run(async()=>{
                var row=Balances.SelectedItem as DataRowView;if(row==null)throw new ArgumentException("حدد الطرف أولًا.");
                long id=System.Convert.ToInt64(row["party_id"]);string name=System.Convert.ToString(row["name"]);
                var from=FromDate.SelectedDate??DateTime.Today;var to=ToDate.SelectedDate??DateTime.Today;
                var data=await Task.Run(()=>_finance.PartyStatement(id,from,to));
                var grid=new DataGrid {IsReadOnly=true,AutoGenerateColumns=false,ItemsSource=data.DefaultView};
                foreach(var column in new [] {new [] {"التاريخ","entry_date"},new [] {"المرجع","source_key"},new [] {"الحساب","account_name"},new [] {"الفترة","brought_forward"}})
                    grid.Columns.Add(new DataGridTextColumn {Header=column[0],Binding=new Binding(column[1])});
                foreach(var column in new [] {new [] {"مدين","debit_cents"},new [] {"دائن","credit_cents"}})
                    grid.Columns.Add(new DataGridTextColumn {Header=column[0],Binding=new Binding(column[1]) {Converter=new FinanceMoneyConverter()}});
                new Window {Owner=this,Title="كشف حساب "+name,FlowDirection=FlowDirection.RightToLeft,Width=950,Height=600,Content=grid,WindowStartupLocation=WindowStartupLocation.CenterOwner}.ShowDialog();
            });
        }
    }
}
