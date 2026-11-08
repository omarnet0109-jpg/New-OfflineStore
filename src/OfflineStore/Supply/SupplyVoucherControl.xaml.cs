using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Threading.Tasks;
using OfflineStore.Models;
using OfflineStore.Services;

namespace OfflineStore.Supply
{
    // UI orchestration only; money, validation and SQL live in SupplyVoucherService.
    public partial class SupplyVoucherControl : UserControl
    {
        private StoreService _store;
        private SupplyVoucherDraft _current=new SupplyVoucherDraft();
        private readonly List<SupplyVoucherLine> _lines=new List<SupplyVoucherLine>();
        private bool _loading;
        private bool _dirty;
        private int _editingIndex=-1;
        private bool _posted;
        public event Action Posted;
        public bool IsBusy { get; private set; }
        private async Task RunBusy(Func<Task> operation)
        {
            if (IsBusy) return;
            IsBusy=true;
            var owner=Window.GetWindow(this);
            bool enabled=owner==null || owner.IsEnabled;
            if(owner!=null) owner.IsEnabled=false;
            try { await operation(); }
            finally { IsBusy=false; if(owner!=null) owner.IsEnabled=enabled; }
        }

        private sealed class LineRow
        {
            public SupplyVoucherLine Line { get; set; }
            public int Number { get; set; }
            public string Name { get { return Line.ItemName; } }
            public string Unit { get { return Line.Unit; } }
            public string Qty { get { return Amounts.Qty(Line.QuantityMilli); } }
            public string Cost { get { return Amounts.Price(Line.UnitCostCents); } }
            public string Discount { get { return Amounts.Price(Line.DiscountCents); } }
            public string Total { get { return Amounts.Price(Line.LineTotalCents); } }
        }

        public SupplyVoucherControl()
        {
            InitializeComponent();
        }

        public void Setup(StoreService store)
        {
            _store=store;
            RefreshSuppliers();
            ProductBox.ItemsSource=_store.Products();
            StartNew(false);
        }
        private void Fail(Exception ex)
        {
            StatusText.Text=ex.Message;
            MessageBox.Show(ex.Message,"سند التوريد",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        private void RefreshSuppliers(long selectId=0)
        {
            if(_store==null)return;
            _loading=true;
            try
            {
                SupplierBox.ItemsSource=_store.Suppliers();
                if(selectId>0)SupplierBox.SelectedValue=selectId;
            }
            finally { _loading=false; }
        }
        private bool ConfirmLoseEdits()
        {
            return !_dirty || MessageBox.Show("فيه تعديلات لم يتم حفظها. هل تريد تجاهلها؟",
                "سند التوريد",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
        }
        public bool ConfirmClosing()
        { return ConfirmLoseEdits(); }
        public void FocusBarcode() { BarcodeBox.Focus(); }
        public void FocusProductSearch()
        {
            if (_store==null || _posted) return;
            ProductBox.Focus();
            ProductBox.IsDropDownOpen=true;
        }
        public void LoadVoucher(long voucherId)
        { LoadToUi(_store.SupplyVouchers.Load(voucherId)); }

        private void StartNew(bool confirm)
        {
            if(confirm&&!ConfirmLoseEdits())return;
            _loading=true;
            try
            {
                _current=new SupplyVoucherDraft();
                _lines.Clear();
                _editingIndex=-1; _posted=false; _dirty=false;
                CodeText.Text="سند جديد — ROF يتولد عند الحفظ";
                VoucherDateBox.SelectedDate=DateTime.Today;
                SupplierInvoiceDateBox.SelectedDate=DateTime.Today;
                SupplierBox.SelectedIndex=-1;
                SupplierInvoiceNoBox.Text="";
                TaxRateBox.Text="0";
                HeaderDiscountBox.Text="0";
                PaidBox.Text="0";
                PaymentTypeBox.SelectedIndex=0;
                NotesBox.Text="";
                BarcodeBox.Text="";
                QtyBox.Text="1";CostBox.Text="0";DiscountBox.Text="0";
                LineDiscountTypeBox.SelectedIndex=0;
                ProductBox.SelectedIndex=-1;
                UnitBox.ItemsSource=null;
                LineHelpText.Text="امسح الباركود لاختيار الصنف، ثم أدخل الكمية وسعر الشراء والخصم.";
                StatusText.Text="سند جديد: الحفظ كمسودة لا يزيد المخزون؛ الاعتماد يضيف الكمية مرة واحدة.";
            }
            finally { _loading=false; }
            SetEditable(true);
            RefreshRows();
            UpdateSummary();
            BarcodeBox.Focus();
        }
        private void SetEditable(bool editable)
        {
            _posted=!editable;
            foreach(var control in new Control[] {
                SupplierBox, SupplierInvoiceNoBox, PaymentTypeBox, TaxRateBox,
                HeaderDiscountBox, BarcodeBox, ProductBox, UnitBox,QtyBox,CostBox,
                DiscountBox,LineDiscountTypeBox, NotesBox
            }) control.IsEnabled=editable;
            VoucherDateBox.IsEnabled=editable;
            SupplierInvoiceDateBox.IsEnabled=editable;
            AddLineButton.IsEnabled=editable;
            NewSupplierButton.IsEnabled=editable;
            SaveDraftButton.IsEnabled=editable;
            ApproveButton.IsEnabled=editable;
            EditDraftButton.IsEnabled=editable;
            DeleteDraftButton.IsEnabled=editable;
            PaidBox.IsEnabled=editable && SelectedPaymentType()=="CREDIT";
        }
        private static string MoneyText(long value)
        { return Amounts.EditablePrice(value); }
        private static string SelectedTag(ComboBox combo)
        {
            var value=combo.SelectedItem as ComboBoxItem;
            return value==null ? "" : (value.Tag as string ?? "");
        }
        private string SelectedPaymentType()
        { return SelectedTag(PaymentTypeBox); }

        private SupplyVoucherLine ParseLine()
        {
            var p=ProductBox.SelectedItem as ProductRow;
            var unit=UnitBox.SelectedItem as ProductUnitRow;
            if(p==null||unit==null)
                throw new ArgumentException("اختر الصنف ووحدة القياس قبل إضافة السطر.");
            var line=new SupplyVoucherLine {
                ProductID=p.Id,ProductUnitID=unit.Id,ItemCode=p.Sku,
                ItemName=p.Name,Unit=unit.Name,UnitFactorMilli=unit.FactorMilli,
                QuantityMilli=Amounts.ToMilli(QtyBox.Text,"الكمية"),
                UnitCostCents=Amounts.ToCents(CostBox.Text,"سعر الشراء"),
                DiscountType=SelectedTag(LineDiscountTypeBox),
                DiscountValue=Amounts.ToCents(DiscountBox.Text,"خصم السطر")
            };
            // Reuse the same calculation logic as the posting transaction.
            var sample=new SupplyVoucherDraft { SupplierID=1,
                PaymentType="CREDIT",VoucherDate=DateTime.Today,
                SupplierInvoiceDate=DateTime.Today,
                Lines=new List<SupplyVoucherLine>{line} };
            SupplyVoucherCalculator.Calculate(sample);
            return line;
        }
        private SupplyVoucherDraft ReadDraft()
        {
            var supplier=SupplierBox.SelectedItem as PartyRow;
            if(supplier==null)throw new ArgumentException("اختر موردًا قبل حفظ السند.");
            if(!VoucherDateBox.SelectedDate.HasValue||!SupplierInvoiceDateBox.SelectedDate.HasValue)
                throw new ArgumentException("حدد تاريخ التوريد وتاريخ فاتورة المورد.");
            var voucher=new SupplyVoucherDraft {
                VoucherID=_current.VoucherID,VoucherCode=_current.VoucherCode,
                VoucherDate=VoucherDateBox.SelectedDate.Value.Date,
                SupplierID=supplier.Id,SupplierInvoiceNo=SupplierInvoiceNoBox.Text.Trim(),
                SupplierInvoiceDate=SupplierInvoiceDateBox.SelectedDate.Value.Date,
                PaymentType=SelectedPaymentType(),
                TaxRateBps=Amounts.ToCents(TaxRateBox.Text,"نسبة الضريبة"),
                HeaderDiscountCents=Amounts.ToCents(HeaderDiscountBox.Text,"خصم السند"),
                PaidAmountCents=SelectedPaymentType()=="CASH" ? 0 :
                    Amounts.ToCents(PaidBox.Text,"المدفوع"),
                Notes=NotesBox.Text??"",
                Lines=new List<SupplyVoucherLine>(_lines),
                State="DRAFT"
            };
            SupplyVoucherCalculator.Calculate(voucher);
            return voucher;
        }
        private void RefreshRows()
        {
            DetailsGrid.ItemsSource=_lines.Select((line,i)=>new LineRow {
                Line=line,Number=i+1
            }).ToList();
        }
        private void UpdateSummary()
        {
            if(_loading||_store==null||GrandTotalText==null)return;
            try
            {
                var preview=new SupplyVoucherDraft {
                    SupplierID=1,
                    VoucherDate=VoucherDateBox.SelectedDate??DateTime.Today,
                    SupplierInvoiceDate=SupplierInvoiceDateBox.SelectedDate??DateTime.Today,
                    PaymentType=SelectedPaymentType(),
                    TaxRateBps=Amounts.ToCents(TaxRateBox.Text,"نسبة الضريبة"),
                    HeaderDiscountCents=Amounts.ToCents(HeaderDiscountBox.Text,"خصم السند"),
                    PaidAmountCents=SelectedPaymentType()=="CASH"?0:
                        Amounts.ToCents(PaidBox.Text,"المدفوع"),
                    Lines=new List<SupplyVoucherLine>(_lines)
                };
                if(preview.Lines.Count==0)
                {
                    SubTotalText.Text="0.00";DiscountTotalText.Text="0.00";
                    TaxAmountText.Text="0.00";GrandTotalText.Text="0.00";
                    RemainingText.Text="0.00";WordsText.Text="";
                    return;
                }
                var amount=SupplyVoucherCalculator.Calculate(preview);
                SubTotalText.Text=Amounts.Price(amount.SubTotalCents);
                DiscountTotalText.Text=Amounts.Price(amount.TotalDiscountCents);
                TaxAmountText.Text=Amounts.Price(amount.TaxAmountCents);
                GrandTotalText.Text=Amounts.Price(amount.GrandTotalCents);
                RemainingText.Text=Amounts.Price(amount.RemainingAmountCents);
                WordsText.Text=amount.TotalWordsAr;
                if(preview.PaymentType=="CASH")
                {
                    // Cash receipt always settles the entire invoice automatically.
                    _loading=true;
                    try { PaidBox.Text=MoneyText(amount.PaidAmountCents); }
                    finally { _loading=false; }
                }
                StatusText.Text=_posted ? "سند معتمد — للقراءة والطباعة فقط." :
                    (_dirty ? "تعديلات لم تحفظ بعد." : "جاهز للحفظ أو الاعتماد.");
            }
            catch(ArgumentException ex)
            {
                WordsText.Text="راجع الإجماليات: "+ex.Message;
            }
            catch(OverflowException)
            {
                WordsText.Text="المبلغ كبير جدًا؛ راجع الكميات والأسعار.";
            }
        }
        private void HeaderChanged(object sender,SelectionChangedEventArgs e)
        {
            if(_loading||_store==null)return;
            _dirty=true;
            PaidBox.IsEnabled=!_posted&&SelectedPaymentType()=="CREDIT";
            if(SelectedPaymentType()=="CREDIT"&&sender==PaymentTypeBox)
            {
                _loading=true;
                try { PaidBox.Text="0"; }
                finally { _loading=false; }
            }
            UpdateSummary();
        }
        private void HeaderTextChanged(object sender,TextChangedEventArgs e)
        {
            if(_loading||_store==null)return;
            _dirty=true;
            UpdateSummary();
        }
        private void LineTextChanged(object sender,TextChangedEventArgs e)
        { if(!_loading&&_store!=null)_dirty=true; }
        private void LineSelectionChanged(object sender,SelectionChangedEventArgs e)
        { if(!_loading&&_store!=null)_dirty=true; }
        private void ProductChanged(object sender,SelectionChangedEventArgs e)
        {
            if(_loading||_store==null)return;
            var product=ProductBox.SelectedItem as ProductRow;
            UnitBox.ItemsSource=product==null ? null : _store.Units(product.Id);
            UnitBox.SelectedIndex=product==null ? -1:0;
        }
        private void UnitChanged(object sender,SelectionChangedEventArgs e)
        {
            if(_loading||_store==null)return;
            var product=ProductBox.SelectedItem as ProductRow;
            var unit=UnitBox.SelectedItem as ProductUnitRow;
            if(product!=null&&unit!=null && product.LastCostCents>0)
            {
                long suggested=checked((long)decimal.Round(
                    (decimal)product.LastCostCents*unit.FactorMilli/1000m,
                    0,MidpointRounding.AwayFromZero));
                CostBox.Text=MoneyText(suggested);
            }
        }
        private void BarcodeKeyDown(object sender,KeyEventArgs e)
        {
            if(e.Key!=Key.Enter)return;
            AcceptBarcode(BarcodeBox.Text);
            e.Handled=true;
        }
        public void AcceptBarcode(string code)
        {
            if(_store==null||_posted)return;
            try
            {
                var lookup=_store.FindUnitByCode(code);
                var products=ProductBox.ItemsSource as IEnumerable<ProductRow>;
                ProductBox.SelectedItem=products.FirstOrDefault(p=>p.Id==lookup.Item2);
                if(ProductBox.SelectedItem==null)
                    throw new InvalidOperationException("الصنف غير متاح في قائمة التوريد.");
                var units=UnitBox.ItemsSource as IEnumerable<ProductUnitRow>;
                UnitBox.SelectedItem=units.FirstOrDefault(u=>u.Id==lookup.Item1.Id);
                BarcodeBox.Text="";
                QtyBox.Text="1";
                QtyBox.Focus();
                QtyBox.SelectAll();
                LineHelpText.Text="تم التعرف على الصنف. أكد الكمية وتكلفة الشراء ثم اضغط إضافة سطر.";
            }
            catch(Exception ex)
            {
                BarcodeBox.SelectAll();
                LineHelpText.Text="باركود غير معروف: "+ex.Message;
            }
        }
        private void AddLineClick(object sender,RoutedEventArgs e)
        {
            if(_posted)return;
            try
            {
                var line=ParseLine();
                if(_editingIndex>=0)
                {
                    _lines[_editingIndex]=line;
                    _editingIndex=-1;
                }
                else _lines.Add(line);
                _dirty=true;
                RefreshRows();
                UpdateSummary();
                QtyBox.Text="1";
                DiscountBox.Text="0";
                AddLineButton.Content="إضافة سطر";
                BarcodeBox.Focus();
            }
            catch(Exception ex){Fail(ex);}
        }
        private void EditLineClick(object sender,RoutedEventArgs e)
        {
            if(_posted)return;
            var row=(sender as Button)?.Tag as LineRow;
            if(row==null)return;
            _editingIndex=_lines.IndexOf(row.Line);
            if(_editingIndex<0)return;
            _loading=true;
            try
            {
                var products=ProductBox.ItemsSource as IEnumerable<ProductRow>;
                ProductBox.SelectedItem=products.FirstOrDefault(p=>p.Id==row.Line.ProductID);
                UnitBox.ItemsSource=_store.Units(row.Line.ProductID);
                var units=UnitBox.ItemsSource as IEnumerable<ProductUnitRow>;
                UnitBox.SelectedItem=units.FirstOrDefault(u=>u.Id==row.Line.ProductUnitID);
                QtyBox.Text=Amounts.Qty(row.Line.QuantityMilli);
                CostBox.Text=MoneyText(row.Line.UnitCostCents);
                DiscountBox.Text=MoneyText(row.Line.DiscountValue);
                LineDiscountTypeBox.SelectedIndex=row.Line.DiscountType=="PERCENT"?1:0;
                AddLineButton.Content="حفظ السطر";
            }
            finally {_loading=false;}
            QtyBox.Focus();
        }
        private void RemoveLineClick(object sender,RoutedEventArgs e)
        {
            if(_posted)return;
            var row=(sender as Button)?.Tag as LineRow;
            if(row==null)return;
            if(_lines.Remove(row.Line))
            {
                _editingIndex=-1; _dirty=true;RefreshRows();UpdateSummary();
            }
        }

        private async Task<SupplyVoucherDraft> SaveCurrentAsync()
        {
            if(_posted)throw new InvalidOperationException("لا يمكن حفظ تعديل على سند معتمد.");
            var draft=ReadDraft();
            draft.Lines=draft.Lines.Select(x=>new SupplyVoucherLine {
                ProductID=x.ProductID,ProductUnitID=x.ProductUnitID,Unit=x.Unit,
                UnitFactorMilli=x.UnitFactorMilli,QuantityMilli=x.QuantityMilli,
                UnitCostCents=x.UnitCostCents,DiscountType=x.DiscountType,DiscountValue=x.DiscountValue
            }).ToList();
            var saved=await Task.Run(()=>_store.SupplyVouchers.SaveDraft(draft));
            _current=saved;
            _dirty=false;
            CodeText.Text=saved.VoucherCode+" — مسودة محفوظة";
            StatusText.Text="تم حفظ المسودة، ولم تتغير أرصدة المخزون بعد.";
            return saved;
        }
        private void NewClick(object sender,RoutedEventArgs e){StartNew(true);}
        private async void SaveDraftClick(object sender,RoutedEventArgs e)
        { try { await RunBusy(async ()=> { await SaveCurrentAsync(); }); } catch(Exception ex) { Fail(ex); } }
        private void EditDraftClick(object sender,RoutedEventArgs e)
        {
            if(_current.State=="POSTED")
            {StatusText.Text="السند المعتمد لا يمكن تعديله. يتم التصحيح بسند عكسي مستقل.";return;}
            SetEditable(true);
            StatusText.Text="عدّل البيانات أو السطور ثم اضغط حفظ مسودة.";
        }
        private async void ApproveClick(object sender,RoutedEventArgs e)
        {
            if(IsBusy || _posted)return;
            if(MessageBox.Show("اعتماد السند سيضيف الكميات للمخزون مرة واحدة. هل تؤكد؟",
                "اعتماد التوريد",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
            try
            {
                await RunBusy(async ()=> {
                    var draft=await SaveCurrentAsync();
                    var posted=await Task.Run(()=>_store.SupplyVouchers.Approve(draft.VoucherID));
                    LoadToUi(posted);
                    StatusText.Text="تم اعتماد "+posted.VoucherCode+" وإضافة المخزون بنجاح.";
                });
                Posted?.Invoke();
                MessageBox.Show("تم اعتماد التوريد وتسجيل حركة المخزون.","تم",MessageBoxButton.OK,MessageBoxImage.Information);
            }
            catch(Exception ex){Fail(ex);}
        }
        private void DeleteDraftClick(object sender,RoutedEventArgs e)
        {
            if(_current.VoucherID==0){StartNew(false);return;}
            if(_posted)return;
            if(MessageBox.Show("حذف المسودة "+_current.VoucherCode+"؟ لا يؤثر على المخزون.",
                "تأكيد الحذف",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
            try { _store.SupplyVouchers.DeleteDraft(_current.VoucherID);StartNew(false); }
            catch(Exception ex){Fail(ex);}
        }

        private void LoadToUi(SupplyVoucherDraft loaded)
        {
            _loading=true;
            try
            {
                _current=loaded;_lines.Clear();_lines.AddRange(loaded.Lines);
                CodeText.Text=loaded.VoucherCode+" — "+
                    (loaded.State=="POSTED"?"معتمد":"مسودة");
                VoucherDateBox.SelectedDate=loaded.VoucherDate;
                SupplierBox.SelectedValue=loaded.SupplierID;
                SupplierInvoiceNoBox.Text=loaded.SupplierInvoiceNo;
                SupplierInvoiceDateBox.SelectedDate=loaded.SupplierInvoiceDate;
                PaymentTypeBox.SelectedIndex=loaded.PaymentType=="CREDIT"?1:0;
                TaxRateBox.Text=MoneyText(loaded.TaxRateBps);
                HeaderDiscountBox.Text=MoneyText(loaded.HeaderDiscountCents);
                PaidBox.Text=MoneyText(loaded.PaidAmountCents);
                NotesBox.Text=loaded.Notes;
                _editingIndex=-1;_dirty=false;
                AddLineButton.Content="إضافة سطر";
            }
            finally {_loading=false;}
            SetEditable(loaded.State!="POSTED");
            RefreshRows();
            UpdateSummary();
        }
        private void SearchClick(object sender,RoutedEventArgs e)
        {
            if(!ConfirmLoseEdits())return;
            try
            {
                var dialog=new Window {
                    Title="بحث في سندات التوريد",Owner=Window.GetWindow(this),
                    WindowStartupLocation=WindowStartupLocation.CenterOwner,
                    Width=700,Height=440,MinWidth=500,MinHeight=300,
                    FlowDirection=FlowDirection.RightToLeft,FontFamily=new FontFamily("Segoe UI")
                };
                var root=new DockPanel { Margin=new Thickness(14) };
                var search=new TextBox { Height=38,Margin=new Thickness(0,0,0,10),
                    ToolTip="كود السند أو اسم المورد أو رقم فاتورة المورد" };
                DockPanel.SetDock(search,Dock.Top);
                root.Children.Add(search);
                var open=new Button{Content="فتح السند المحدد",Height=39,
                    Margin=new Thickness(0,10,0,0)};
                DockPanel.SetDock(open,Dock.Bottom);
                root.Children.Add(open);
                var list=new ListBox { DisplayMemberPath="Display" };
                root.Children.Add(list);
                Action refresh=()=>list.ItemsSource=_store.SupplyVouchers.Search(search.Text);
                refresh();
                search.TextChanged+=(source,args)=>refresh();
                Action choose=()=>{
                    if(list.SelectedItem!=null)dialog.DialogResult=true;
                };
                open.Click+=(source,args)=>choose();
                list.MouseDoubleClick+=(source,args)=>choose();
                dialog.Content=root;
                if(dialog.ShowDialog()==true)
                {
                    var chosen=list.SelectedItem as SupplyVoucherSummary;
                    if(chosen!=null)LoadToUi(_store.SupplyVouchers.Load(chosen.VoucherID));
                }
            }
            catch(Exception ex){Fail(ex);}
        }

        private void AddSupplierClick(object sender,RoutedEventArgs e)
        {
            if(_posted)return;
            var dialog=new Window {
                Title="إضافة مورد",Owner=Window.GetWindow(this),
                WindowStartupLocation=WindowStartupLocation.CenterOwner,
                Width=405,Height=205,MinWidth=360,ResizeMode=ResizeMode.NoResize,
                FlowDirection=FlowDirection.RightToLeft,FontFamily=new FontFamily("Segoe UI")
            };
            var root=new StackPanel{Margin=new Thickness(18)};
            root.Children.Add(new TextBlock{Text="اسم المورد الجديد",Margin=new Thickness(0,0,0,7)});
            var name=new TextBox {Height=38,MaxLength=120,Margin=new Thickness(0,0,0,15)};
            root.Children.Add(name);
            var save=new Button{Content="حفظ المورد",Height=40};
            save.Click+=(source,args)=>{if(!string.IsNullOrWhiteSpace(name.Text))dialog.DialogResult=true;};
            root.Children.Add(save);
            dialog.Content=root;
            name.Focus();
            if(dialog.ShowDialog()!=true)return;
            try
            {
                string typed=name.Text.Trim();
                var found=_store.Suppliers().FirstOrDefault(
                    x=>string.Equals(x.Name,typed,StringComparison.CurrentCultureIgnoreCase));
                if(found==null)_store.AddSupplier(typed,null);
                var selected=_store.Suppliers().FirstOrDefault(
                    x=>string.Equals(x.Name,typed,StringComparison.CurrentCultureIgnoreCase));
                RefreshSuppliers(selected==null?0:selected.Id);
                _dirty=true;
                StatusText.Text="تم اختيار المورد، أكمل بيانات السند.";
            }
            catch(Exception ex){Fail(ex);}
        }

        private void PrintClick(object sender,RoutedEventArgs e)
        {
            try
            {
                if(_current.VoucherID==0 || _current.State!="POSTED")
                    throw new InvalidOperationException("اعتمد السند أولًا قبل طباعة مستند توريد رسمي.");
                var voucher=_store.SupplyVouchers.Load(_current.VoucherID);
                var totals=SupplyVoucherCalculator.Calculate(voucher);
                var doc=new FlowDocument {
                    FlowDirection=FlowDirection.RightToLeft,
                    FontFamily=new FontFamily("Segoe UI"),FontSize=13,
                    PagePadding=new Thickness(35),ColumnWidth=700
                };
                var title=new Paragraph(new Run("سند توريد واستلام | "+voucher.VoucherCode)){
                    FontSize=20,FontWeight=FontWeights.Bold,TextAlignment=TextAlignment.Center
                };
                doc.Blocks.Add(title);
                doc.Blocks.Add(new Paragraph(new Run("التاريخ: "+voucher.VoucherDate.ToString("yyyy-MM-dd")+
                    "   |   المورد: "+_store.Suppliers().First(x=>x.Id==voucher.SupplierID).Name)));
                doc.Blocks.Add(new Paragraph(new Run("فاتورة المورد: "+voucher.SupplierInvoiceNo+
                    "   |   تاريخها: "+voucher.SupplierInvoiceDate.ToString("yyyy-MM-dd"))));
                var table=new Table { CellSpacing=0 };
                foreach(var w in new double[]{130,120,72,75,85,85})
                    table.Columns.Add(new TableColumn{Width=new GridLength(w)});
                var group=new TableRowGroup();
                table.RowGroups.Add(group);
                string[] headers={"الصنف","الوحدة","الكمية","سعر الشراء","الخصم","الصافي"};
                var heading=new TableRow();
                foreach(var header in headers)heading.Cells.Add(new TableCell(new Paragraph(new Run(header))){
                    FontWeight=FontWeights.Bold});
                group.Rows.Add(heading);
                foreach(var x in voucher.Lines)
                {
                    var row=new TableRow();
                    foreach(var value in new []{x.ItemName,x.Unit,Amounts.Qty(x.QuantityMilli),
                         Amounts.Price(x.UnitCostCents),Amounts.Price(x.DiscountCents),
                         Amounts.Price(x.LineTotalCents)})
                        row.Cells.Add(new TableCell(new Paragraph(new Run(value))));
                    group.Rows.Add(row);
                }
                doc.Blocks.Add(table);
                doc.Blocks.Add(new Paragraph(new Run("قبل الخصم: "+Amounts.Price(totals.SubTotalCents)+
                    " | الخصم: "+Amounts.Price(totals.TotalDiscountCents)+
                    " | الضريبة: "+Amounts.Price(totals.TaxAmountCents))));
                doc.Blocks.Add(new Paragraph(new Run("الإجمالي: "+Amounts.Price(totals.GrandTotalCents)+
                    " | المدفوع: "+Amounts.Price(totals.PaidAmountCents)+
                    " | المتبقي: "+Amounts.Price(totals.RemainingAmountCents))) {
                        FontSize=15,FontWeight=FontWeights.Bold
                    });
                doc.Blocks.Add(new Paragraph(new Run(totals.TotalWordsAr)));
                if(!string.IsNullOrWhiteSpace(voucher.Notes))
                    doc.Blocks.Add(new Paragraph(new Run("ملاحظات: "+voucher.Notes)));
                var print=new PrintDialog();
                if(print.ShowDialog()==true)
                    print.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,
                        "سند توريد "+voucher.VoucherCode);
            }
            catch(Exception ex){Fail(ex);}
        }
    }
}