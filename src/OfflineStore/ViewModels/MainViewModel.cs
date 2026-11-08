using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Threading.Tasks;
using Microsoft.Win32;
using OfflineStore.Models;
using OfflineStore.Appearance;
using OfflineStore.Services;

namespace OfflineStore.ViewModels
{
    public sealed class RelayCommand : ICommand
    {
        private readonly Action _execute;
        public RelayCommand(Action execute) { _execute = execute; }
        public event EventHandler CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object parameter) { return true; }
        public void Execute(object parameter) { _execute(); }
    }

    public sealed class MainViewModel : INotifyPropertyChanged
    {
        private readonly StoreService _service;
        private bool _posting;
        private DraftLine _editingSaleLine;
        private DraftLine _editingPurchaseLine;
        public bool IsEditingSaleLine { get { return _editingSaleLine != null; } }
        public bool IsEditingPurchaseLine { get { return _editingPurchaseLine != null; } }
        public string SaleLineActionText { get { return IsEditingSaleLine ? "حفظ تعديل السطر" : "إضافة للفاتورة"; } }
        public string PurchaseLineActionText { get { return IsEditingPurchaseLine ? "حفظ تعديل السطر" : "إضافة للفاتورة"; } }
        public event Action<string> SalePosted;
        private DocumentRow _selectedDocument;
        public DocumentRow SelectedDocument
        {
            get { return _selectedDocument; }
            set { _selectedDocument = value; Raise(); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
        private void Raise([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null) handler(this, new PropertyChangedEventArgs(name));
        }
        private void Run(Action operation)
        {
            try { operation(); }
            catch (Exception e)
            {
#if SCREENSHOT_BUILD
                // Headless Windows smoke tests must fail immediately instead of
                // waiting forever for an invisible warning dialog.
                throw;
#else
                MessageBox.Show(e.Message, "تعذر إتمام العملية", MessageBoxButton.OK, MessageBoxImage.Warning);
#endif
            }
        }
        public bool IsBusy { get; private set; }
        public bool IsIdle { get { return !IsBusy; } }
        private async Task RunLong(Func<Task> operation)
        {
            if (IsBusy) return;
            IsBusy=true; Raise(nameof(IsBusy)); Raise(nameof(IsIdle));
            try { await operation(); }
            catch (Exception ex)
            {
#if SCREENSHOT_BUILD
                throw;
#else
                MessageBox.Show(ex.Message,"تعذر إتمام العملية",MessageBoxButton.OK,MessageBoxImage.Warning);
#endif
            }
            finally { IsBusy=false; Raise(nameof(IsBusy)); Raise(nameof(IsIdle)); }
        }

        private void Inform(string message)
        {
            MessageBox.Show(message, "تم بنجاح", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        public ObservableCollection<LowStockRow> LowStock { get; } = new ObservableCollection<LowStockRow>();
        public ObservableCollection<ProductUnitRow> PurchaseUnits { get; } = new ObservableCollection<ProductUnitRow>();
        public ObservableCollection<ProductUnitRow> SaleUnits { get; } = new ObservableCollection<ProductUnitRow>();
        public ObservableCollection<ProductUnitRow> CatalogUnits { get; } = new ObservableCollection<ProductUnitRow>();
        public string CurrentEmployee { get { return _service.CurrentUser.DisplayName + " | " + _service.CurrentUser.Role; } }
        public bool CanAppearance { get { return _service.Can("APPEARANCE"); } }
        public bool CanCatalog { get { return _service.Can("CATALOG"); } }
        public bool CanPurchase { get { return _service.Can("PURCHASE"); } }
        public bool CanSale { get { return _service.Can("SALE"); } }
        public bool CanUsers { get { return _service.Can("USERS"); } }
        public bool CanReports { get { return _service.Can("REPORTS"); } }
        public bool CanBackup { get { return _service.Can("BACKUP"); } }
        public bool CanLowStock { get { return _service.Can("LOW_STOCK"); } }
        public bool IsSalePriceReadOnly { get { return !_service.Can("OVERRIDE_PRICE"); } }
        public string LowStockCount { get { return LowStock.Count.ToString(); } }
        public string SaleStockHint
        {
            get
            {
                if (SaleProduct == null) return "اختر صنفًا لمعرفة الرصيد المتاح.";
                long inCart = 0;
                foreach (var line in SaleLines.Where(x => x.ProductId == SaleProduct.Id))
                    inCart = checked(inCart + line.QuantityMilli);
                long remaining = SaleProduct.StockMilli - inCart;
                return "الرصيد بالوحدة الأساسية: " + Amounts.Qty(SaleProduct.StockMilli) + " " + SaleProduct.Unit
                    + " | محجوز بالفاتورة: " + Amounts.Qty(inCart)
                    + " | المتبقي: " + Amounts.Qty(remaining) + " " + SaleProduct.Unit;
            }
        }
        // Show the complete calculation *before* adding a sale line.
        // The stock balance is in the product's base unit, whereas the
        // quantity field is expressed in the selected package. Never present
        // an available "200" as if it meant 200 cartons.
        private void RaiseSaleAvailability()
        {
            Raise(nameof(SaleStockHint));
            Raise(nameof(SaleQuantityPreview));
            Raise(nameof(IsSaleQuantityInsufficient));
        }

        private long SaleAvailableBaseMilli()
        {
            if (SaleProduct == null) return 0;
            long reserved = SaleLines.Where(x => x.ProductId == SaleProduct.Id
                    && !ReferenceEquals(x, _editingSaleLine))
                .Aggregate(0L, (sum, line) => checked(sum + line.QuantityMilli));
            return checked(SaleProduct.StockMilli - reserved);
        }

        public bool IsSaleQuantityInsufficient
        {
            get
            {
                if (SaleProduct == null || SaleUnit == null) return false;
                try
                {
                    var entered = Amounts.ToMilli(SaleQty, "الكمية");
                    var requested = Amounts.BaseQuantity(entered, SaleUnit.FactorMilli);
                    return requested > SaleAvailableBaseMilli();
                }
                catch (ArgumentException) { return false; }
                catch (OverflowException) { return false; }
            }
        }

        public string SaleQuantityPreview
        {
            get
            {
                if (SaleProduct == null || SaleUnit == null)
                    return "اختر الصنف والوحدة لمعرفة الكمية المطلوبة من المخزون.";
                var factor = Amounts.Qty(SaleUnit.FactorMilli);
                try
                {
                    var entered = Amounts.ToMilli(SaleQty, "الكمية");
                    var requested = Amounts.BaseQuantity(entered, SaleUnit.FactorMilli);
                    var available = SaleAvailableBaseMilli();
                    return "كل " + SaleUnit.Name + " = " + factor + " " + SaleProduct.Unit
                        + "  |  المطلوب: " + Amounts.Qty(entered) + " × " + factor
                        + " = " + Amounts.Qty(requested) + " " + SaleProduct.Unit
                        + "  |  المتاح: " + Amounts.Qty(available) + " " + SaleProduct.Unit
                        + (requested > available
                            ? "  — الكمية تتجاوز المخزون، اختر وحدة أصغر أو قلل العدد."
                            : "  — الكمية متاحة.");
                }
                catch (ArgumentException)
                {
                    return "راجع الكمية المدخلة. كل " + SaleUnit.Name
                        + " تساوي " + factor + " " + SaleProduct.Unit + ".";
                }
                catch (OverflowException)
                {
                    return "الكمية كبيرة جدًا. قلّل العدد أو اختر وحدة أصغر.";
                }
            }
        }

        public string LowStockBanner { get { return LowStock.Count==0 ? "لا توجد تنبيهات نقص (للأصناف التي لها حد إعادة طلب)." :
            "تنبيه: " + LowStock.Count + " صنف وصل أو انخفض عن حد إعادة الطلب!"; } }
        public ObservableCollection<ProductRow> Products { get; } = new ObservableCollection<ProductRow>();
        public ObservableCollection<PartyRow> Customers { get; } = new ObservableCollection<PartyRow>();
        public ObservableCollection<PartyRow> Suppliers { get; } = new ObservableCollection<PartyRow>();
        public ObservableCollection<DraftLine> PurchaseLines { get; } = new ObservableCollection<DraftLine>();
        public ObservableCollection<DraftLine> SaleLines { get; } = new ObservableCollection<DraftLine>();
        public ObservableCollection<DocumentRow> Documents { get; } = new ObservableCollection<DocumentRow>();
        public ObservableCollection<MovementRow> Movements { get; } = new ObservableCollection<MovementRow>();

        private ProductRow _purchaseProduct;
        public ProductRow PurchaseProduct
        {
            get { return _purchaseProduct; }
            set { _purchaseProduct = value; Raise(); LoadUnits(PurchaseUnits,value); PurchaseUnit = PurchaseUnits.FirstOrDefault(); }
        }
        private ProductRow _saleProduct;
        public ProductRow SaleProduct
        {
            get { return _saleProduct; }
            set { _saleProduct = value; Raise(); LoadUnits(SaleUnits,value); SaleUnit = SaleUnits.FirstOrDefault(); RaiseSaleAvailability(); }
        }
        private ProductUnitRow _purchaseUnit;
        public ProductUnitRow PurchaseUnit
        {
            get { return _purchaseUnit; }
            set { _purchaseUnit=value; Raise(); if(value!=null && PurchaseProduct!=null)
                PurchasePrice=Amounts.EditablePrice(checked(PurchaseProduct.LastCostCents*(value.FactorMilli/1000))); }
        }
        private ProductUnitRow _saleUnit;
        public ProductUnitRow SaleUnit
        {
            get { return _saleUnit; }
            set { _saleUnit=value; Raise(); RefreshSalePrice(); RaiseSaleAvailability(); }
        }
        private string _salePriceTier = "RETAIL";
        public string SalePriceTier
        {
            get { return _salePriceTier; }
            set
            {
                _salePriceTier=string.IsNullOrEmpty(value) ? "RETAIL" : value;
                Raise();
                RefreshSalePrice();
            }
        }
        private void RefreshSalePrice()
        {
            if (SaleUnit != null)
                SalePrice=Amounts.EditablePrice(SaleUnit.PriceForLevel(SalePriceTier));
        }
        private ProductRow _catalogProduct;
        public ProductRow CatalogProduct
        {
            get { return _catalogProduct; }
            set { _catalogProduct=value; Raise(); LoadUnits(CatalogUnits,value); CatalogUnit=CatalogUnits.FirstOrDefault(); }
        }
        private ProductUnitRow _catalogUnit;
        public ProductUnitRow CatalogUnit
        {
            get { return _catalogUnit; }
            set
            {
                _catalogUnit=value; Raise();
                if(value!=null)
                {
                    UnitNewPrice=Amounts.EditablePrice(value.SalePriceCents);
                    UnitSemiPrice=Amounts.EditablePrice(value.SemiPriceCents);
                    UnitWholesalePrice=Amounts.EditablePrice(value.WholesalePriceCents);
                }
            }
        }
        private void LoadUnits(ObservableCollection<ProductUnitRow> target, ProductRow product)
        { Replace(target, product==null ? new List<ProductUnitRow>() : _service.Units(product.Id)); }
        private string _packageName="علبة";
        public string PackageName {get {return _packageName;} set {_packageName=value; Raise();}}
        private string _packageFactor="12";
        public string PackageFactor {get {return _packageFactor;} set {_packageFactor=value; Raise();}}
        private string _packagePrice="0";
        public string PackagePrice {get {return _packagePrice;} set {_packagePrice=value; Raise();}}
        private string _unitNewPrice="0";
        public string UnitNewPrice {get {return _unitNewPrice;} set {_unitNewPrice=value; Raise();}}
        private string _unitSemiPrice="0";
        public string UnitSemiPrice {get {return _unitSemiPrice;} set {_unitSemiPrice=value; Raise();}}
        private string _unitWholesalePrice="0";
        public string UnitWholesalePrice {get {return _unitWholesalePrice;} set {_unitWholesalePrice=value; Raise();}}
        private string _newReorder="5";
        public string NewReorder {get {return _newReorder;} set {_newReorder=value; Raise();}}
        private string _reorderEdit="5";
        public string ReorderEdit {get {return _reorderEdit;} set {_reorderEdit=value; Raise();}}
        private PartyRow _selectedSupplier;
        public PartyRow SelectedSupplier { get { return _selectedSupplier; } set { _selectedSupplier = value; Raise(); } }
        private DraftLine _selectedPurchaseLine;
        public DraftLine SelectedPurchaseLine { get { return _selectedPurchaseLine; } set { _selectedPurchaseLine = value; Raise(); } }
        private DraftLine _selectedSaleLine;
        public DraftLine SelectedSaleLine { get { return _selectedSaleLine; } set { _selectedSaleLine = value; Raise(); } }

        private string _newBarcode = "";
        public string NewBarcode {get {return _newBarcode;} set {_newBarcode=value;Raise();}}
        private string _packageBarcode = "";
        public string PackageBarcode {get {return _packageBarcode;} set {_packageBarcode=value;Raise();}}
        private string _saleScan = "";
        public string SaleScan {get {return _saleScan;} set {_saleScan=value;Raise();}}
        public string LastBarcodeResult { get; private set; } = "لم يتم مسح باركود بعد.";
        public bool ApplyScannedBarcode(string barcode)
        {
            // Preview only: never modify cart, stock, or invoice from a COM callback.
            if (!CanSale) return false;
            SaleScan = barcode;
            try
            {
                var match = _service.FindUnitByCode(barcode);
                SaleProduct = Products.FirstOrDefault(p => p.Id == match.Item2);
                if (SaleProduct == null) throw new ArgumentException("الصنف غير نشط.");
                SaleUnit = SaleUnits.FirstOrDefault(u => u.Id == match.Item1.Id);
                if (SaleUnit == null) throw new ArgumentException("الوحدة غير نشطة.");
                LastBarcodeResult = "تم العثور على " + SaleProduct.Name + " - " + SaleUnit.Name + " - " + SalePrice + " جنيه";
                Raise(nameof(LastBarcodeResult));
                return true;
            }
            catch (Exception ex)
            {
                // A failed scan must never leave the previous item ready to add.
                SaleProduct = null;
                LastBarcodeResult = "باركود غير معروف: " + barcode + " (" + ex.Message + ")";
                Raise(nameof(LastBarcodeResult));
                return false;
            }
        }
        private long _editingProductId;
        private string _editingProductName = "";
        private string _catalogSupplierText = "";
        public string CatalogSupplierText
        {
            get { return _catalogSupplierText; }
            set { _catalogSupplierText = value; Raise(); }
        }
        public bool IsEditingProduct { get { return _editingProductId > 0; } }
        public bool IsCreatingProduct { get { return _editingProductId == 0; } }
        public bool IsUnitEditable { get { return _editingProductId == 0; } }
        public string CatalogFormTitle
        {
            get { return _editingProductId > 0 ? "تعديل: " + _editingProductName + " (الرصيد محفوظ)" : "إضافة صنف جديد"; }
        }
        private void ResetProductFields()
        {
            _editingProductId = 0;
            _editingProductName = "";
            NewSku = ""; NewName = ""; NewBarcode = "";
            NewUnit = "قطعة"; NewPrice = "0"; NewCost = "0";
            NewReorder = "5"; CatalogSupplierText = "";
            Raise(nameof(IsEditingProduct));
            Raise(nameof(IsCreatingProduct));
            Raise(nameof(IsUnitEditable));
            Raise(nameof(CatalogFormTitle));
        }
        public void StartEditProduct()
        {
            if (!CanCatalog) throw new UnauthorizedAccessException("تعديل الأصناف للمدير فقط.");
            if (CatalogProduct == null) throw new ArgumentException("حدد الصنف من الجدول أولاً.");
            var product = CatalogProduct;
            _editingProductId = product.Id;
            _editingProductName = product.Name;
            NewSku = product.Sku;
            NewName = product.Name;
            NewBarcode = product.Barcode ?? "";
            NewUnit = product.Unit;
            NewPrice = Amounts.EditablePrice(product.SalePriceCents);
            NewCost = Amounts.EditablePrice(product.LastCostCents);
            NewReorder = Amounts.Qty(product.ReorderMilli);
            CatalogSupplierText = product.PreferredSupplier ?? "";
            Raise(nameof(IsEditingProduct));
            Raise(nameof(IsCreatingProduct));
            Raise(nameof(IsUnitEditable));
            Raise(nameof(CatalogFormTitle));
        }
        private string _newSku = "";
        public string NewSku { get { return _newSku; } set { _newSku = value; Raise(); } }
        private string _newName = "";
        public string NewName { get { return _newName; } set { _newName = value; Raise(); } }
        private string _newUnit = "قطعة";
        public string NewUnit { get { return _newUnit; } set { _newUnit = value; Raise(); } }
        private string _newPrice = "0";
        public string NewPrice { get { return _newPrice; } set { _newPrice = value; Raise(); } }
        private string _newCost = "0";
        public string NewCost { get { return _newCost; } set { _newCost = value; Raise(); } }
        private string _supplierName = "";
        public string SupplierName { get { return _supplierName; } set { _supplierName = value; Raise(); } }
        private string _supplierPhone = "";
        public string SupplierPhone { get { return _supplierPhone; } set { _supplierPhone = value; Raise(); } }
        private string _purchaseQty = "1";
        public string PurchaseQty { get { return _purchaseQty; } set { _purchaseQty = value; Raise(); } }
        private string _purchasePrice = "0";
        public string PurchasePrice { get { return _purchasePrice; } set { _purchasePrice = value; Raise(); } }
        private string _saleQty = "1";
        public string SaleQty { get { return _saleQty; } set { _saleQty = value; Raise(); RaiseSaleAvailability(); } }
        private string _salePrice = "0";
        public string SalePrice { get { return _salePrice; } set { _salePrice = value; Raise(); } }
        private PartyRow _saleCustomer;
        public PartyRow SaleCustomer {get {return _saleCustomer;}set {_saleCustomer=value;Raise();}}
        private bool _creditSale;
        public bool CreditSale {get {return _creditSale;}set {_creditSale=value;Raise();}}
        private string _salePaid="0";
        public string SalePaid {get {return _salePaid;}set {_salePaid=value;Raise();}}
        public string PurchaseTotal { get { return Amounts.Price(Total(PurchaseLines)); } }
        public string SaleTotal { get { return Amounts.Price(Total(SaleLines)); } }
        public string DatabasePath { get { return _service.DatabasePath; } }
        public string ProductCount { get { return Products.Count.ToString(); } }
        public string SupplierCount { get { return Suppliers.Count.ToString(); } }
        private string _selectedTheme = "light";
        public string SelectedTheme { get { return _selectedTheme; } set { _selectedTheme = value; Raise(); } }
        private string _selectedAccent = "blue";
        public string SelectedAccent { get { return _selectedAccent; } set { _selectedAccent = value; Raise(); } }

        private static long Total(IEnumerable<DraftLine> lines)
        {
            return lines.Aggregate(0L, (s, l) => checked(s + l.LineTotalCents));
        }
        private void TotalsChanged() { Raise(nameof(PurchaseTotal)); Raise(nameof(SaleTotal)); }

        public ICommand CreateProductCommand { get; private set; }
        public ICommand StartEditProductCommand { get; private set; }
        public ICommand SaveProductChangesCommand { get; private set; }
        public ICommand ResetProductFormCommand { get; private set; }
        public ICommand CreateSupplierCommand { get; private set; }
        public ICommand AddPackageCommand {get;private set;}
        public ICommand ScanSaleCommand {get;private set;}
        public ICommand SetUnitPriceCommand {get;private set;}
        public ICommand UpdateReorderCommand {get;private set;}
        public ICommand AddPurchaseLineCommand { get; private set; }
        public ICommand AddSaleLineCommand { get; private set; }
        public ICommand RemovePurchaseLineCommand { get; private set; }
        public ICommand RemoveSaleLineCommand { get; private set; }
        public ICommand EditPurchaseLineCommand { get; private set; }
        public ICommand EditSaleLineCommand { get; private set; }
        public ICommand CancelPurchaseLineEditCommand { get; private set; }
        public ICommand CancelSaleLineEditCommand { get; private set; }
        public ICommand PostPurchaseCommand { get; private set; }
        public ICommand PostSaleCommand { get; private set; }
        public ICommand RefreshCommand { get; private set; }
        public ICommand BackupCommand { get; private set; }
        public ICommand SaveAppearanceCommand { get; private set; }

        public MainViewModel(StoreService service)
        {
            _service = service;
            ResetProductFormCommand = new RelayCommand(() => ResetProductFields());
            StartEditProductCommand = new RelayCommand(() => Run(StartEditProduct));
            SaveProductChangesCommand = new RelayCommand(() => Run(() =>
            {
                if (_editingProductId == 0) throw new ArgumentException("اختر الصنف واضغط تعديل المحدد أولاً.");
                _service.UpdateProduct(_editingProductId, NewSku, NewName,
                    Amounts.ToCents(NewPrice, "سعر البيع"),
                    Amounts.ToCents(NewCost, "سعر الشراء"),
                    ParseReorder(NewReorder), NewBarcode, CatalogSupplierText);
                Reload();
                ResetProductFields();
                Inform("تم حفظ تعديل الصنف. الفواتير والمخزون السابقان لم يتغيرا.");
            }));
            CreateProductCommand = new RelayCommand(() => Run(() =>
            {
                if (IsEditingProduct) throw new ArgumentException("اضغط صنف جديد لإنهاء وضع التعديل.");
                _service.AddProduct(NewSku, NewName, NewUnit,
                    Amounts.ToCents(NewPrice, "سعر البيع"), Amounts.ToCents(NewCost, "سعر الشراء"),
                    ParseReorder(NewReorder), NewBarcode, CatalogSupplierText);
                ResetProductFields();
                Reload(); Inform("تم إضافة الصنف.");
            }));
            AddPackageCommand = new RelayCommand(() => Run(() =>
            {
                if(CatalogProduct==null) throw new ArgumentException("حدد الصنف أولًا.");
                var factor = Amounts.ToMilli(PackageFactor, "عدد القطع في الوحدة");
                _service.AddUnit(CatalogProduct.Id,PackageName,factor,Amounts.ToCents(PackagePrice,"سعر الوحدة"),PackageBarcode);
                PackageBarcode="";
                Reload(); Inform("تم إضافة وحدة البيع للصنف.");
            }));
            SetUnitPriceCommand = new RelayCommand(() => Run(() =>
            {
                if(CatalogUnit==null) throw new ArgumentException("حدد وحدة البيع أولًا.");
                _service.SetAllUnitPrices(CatalogUnit.Id,
                    Amounts.ToCents(UnitNewPrice,"سعر القطاعي"),
                    Amounts.ToCents(UnitSemiPrice,"سعر نص القطاعي"),
                    Amounts.ToCents(UnitWholesalePrice,"سعر الجملة"));
                Reload(); Inform("تم تحديث السعر، والفواتير القديمة محفوظة بسعرها التاريخي.");
            }));
            UpdateReorderCommand = new RelayCommand(() => Run(() =>
            {
                if(CatalogProduct==null) throw new ArgumentException("حدد الصنف أولًا.");
                _service.SetReorderLevel(CatalogProduct.Id,ParseReorder(ReorderEdit));
                Reload(); Inform("تم تحديث حد تنبيه المخزون.");
            }));
            ScanSaleCommand = new RelayCommand(() => Run(() =>
            {
                if (!ApplyScannedBarcode(SaleScan))
                    throw new ArgumentException(LastBarcodeResult);
                SaleScan = "";
            }));
            CreateSupplierCommand = new RelayCommand(() => Run(() =>
            {
                _service.AddSupplier(SupplierName, SupplierPhone);
                SupplierName = ""; SupplierPhone = "";
                Reload(); Inform("تم إضافة المورد.");
            }));
            AddPurchaseLineCommand = new RelayCommand(() => Run(() => AddLine(true)));
            AddSaleLineCommand = new RelayCommand(() => Run(() => AddLine(false)));
            EditPurchaseLineCommand = new RelayCommand(() => Run(() => BeginEditLine(true)));
            EditSaleLineCommand = new RelayCommand(() => Run(() => BeginEditLine(false)));
            CancelPurchaseLineEditCommand = new RelayCommand(() => CancelEditLine(true));
            CancelSaleLineEditCommand = new RelayCommand(() => CancelEditLine(false));
            RemovePurchaseLineCommand = new RelayCommand(() => Run(() =>
            {
                if (SelectedPurchaseLine != null)
                {
                    if (ReferenceEquals(SelectedPurchaseLine, _editingPurchaseLine)) CancelEditLine(true);
                    PurchaseLines.Remove(SelectedPurchaseLine);
                }
                TotalsChanged();
            }));
            RemoveSaleLineCommand = new RelayCommand(() => Run(() =>
            {
                if (SelectedSaleLine != null)
                {
                    if (ReferenceEquals(SelectedSaleLine, _editingSaleLine)) CancelEditLine(false);
                    SaleLines.Remove(SelectedSaleLine);
                }
                TotalsChanged(); RaiseSaleAvailability();
            }));
            PostPurchaseCommand = new RelayCommand(async () => await RunLong(() => PostAsync(true)));
            PostSaleCommand = new RelayCommand(async () => await RunLong(() => PostAsync(false)));
            RefreshCommand = new RelayCommand(async () => await RunLong(ReloadAsync));
            BackupCommand = new RelayCommand(async () => await RunLong(async () =>
            {
                // Never allow the regular UI to save a backup to the Windows drive.
                // The configured external destination is validated every time.
                var file = await Task.Run(() => _service.CreateExternalBackup());
                Inform("تم التحقق من النسخة الاحتياطية على القرص الخارجي:\\n" + file);
            }));
            var current = _service.Appearance.Load();
            SelectedTheme = current.Theme;
            SelectedAccent = current.Accent;
            SaveAppearanceCommand = new RelayCommand(() => Run(() =>
            {
                _service.Appearance.Save(SelectedTheme, SelectedAccent);
                ThemeManager.Apply(new AppearanceSettings { Theme = SelectedTheme, Accent = SelectedAccent });
                Inform("تم حفظ إعدادات الشكل والألوان.");
            }));
            Reload();
        }

        private static long ParseReorder(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            return Amounts.ToMilli(text,"حد إعادة الطلب", true);
        }
        private void CancelEditLine(bool purchase)
        {
            if (purchase) _editingPurchaseLine = null;
            else _editingSaleLine = null;
            Raise(nameof(IsEditingPurchaseLine)); Raise(nameof(IsEditingSaleLine));
            Raise(nameof(PurchaseLineActionText)); Raise(nameof(SaleLineActionText));
            RaiseSaleAvailability();
        }

        private void BeginEditLine(bool purchase)
        {
            var draft = purchase ? SelectedPurchaseLine : SelectedSaleLine;
            if (draft == null) throw new ArgumentException("حدد سطرًا من الفاتورة أولًا.");
            var product = Products.FirstOrDefault(p => p.Id == draft.ProductId);
            if (product == null) throw new InvalidOperationException("الصنف لم يعد متاحًا.");
            if (purchase)
            {
                PurchaseProduct = product;
                PurchaseUnit = PurchaseUnits.FirstOrDefault(u => u.Id == draft.ProductUnitId);
                if (PurchaseUnit == null) throw new InvalidOperationException("وحدة السطر لم تعد موجودة.");
                PurchaseQty = Amounts.Qty(draft.EnteredQuantityMilli);
                PurchasePrice = Amounts.EditablePrice(draft.PriceCents);
                _editingPurchaseLine = draft;
            }
            else
            {
                SaleProduct = product;
                SaleUnit = SaleUnits.FirstOrDefault(u => u.Id == draft.ProductUnitId);
                if (SaleUnit == null) throw new InvalidOperationException("وحدة السطر لم تعد موجودة.");
                SalePriceTier = draft.PriceLevel ?? "RETAIL";
                SaleQty = Amounts.Qty(draft.EnteredQuantityMilli);
                SalePrice = Amounts.EditablePrice(draft.PriceCents);
                _editingSaleLine = draft;
                RaiseSaleAvailability();
            }
            Raise(nameof(IsEditingPurchaseLine)); Raise(nameof(IsEditingSaleLine));
            Raise(nameof(PurchaseLineActionText)); Raise(nameof(SaleLineActionText));
        }

        private void AddLine(bool purchase)
        {
            var product = purchase ? PurchaseProduct : SaleProduct;
            if (product == null) throw new ArgumentException("حدد الصنف.");
            var unit = purchase ? PurchaseUnit : SaleUnit;
            if (unit==null) throw new ArgumentException("حدد وحدة البيع أو الشراء.");
            var entered = Amounts.ToMilli(purchase ? PurchaseQty : SaleQty, "الكمية");
            var qty = Amounts.BaseQuantity(entered,unit.FactorMilli);
            var price = Amounts.ToCents(purchase ? PurchasePrice : SalePrice, "السعر");
            if (product.IsCountable && qty % 1000 != 0)
                throw new ArgumentException("هذا الصنف يُباع بالعدد الكامل فقط؛ أدخل كمية صحيحة.");
            if (!purchase && IsSalePriceReadOnly && price != unit.PriceForLevel(SalePriceTier))
                throw new UnauthorizedAccessException("الكاشير لا يملك صلاحية تغيير السعر المحدد.");
            if (!purchase && IsSalePriceReadOnly && price == 0)
                throw new UnauthorizedAccessException("سعر الصنف صفر؛ اطلب من المدير ضبط السعر.");
            if (!purchase)
            {
                long already = SaleLines.Where(x => x.ProductId == product.Id &&
                    !ReferenceEquals(x, _editingSaleLine))
                    .Aggregate(0L, (sum, line) => checked(sum + line.QuantityMilli));
                long available = checked(product.StockMilli - already);
                // Preserve the historical checked safety guard as well as its
                // QA contract; only the explanation is changing.
                if (checked(already + qty) > product.StockMilli)
                {
                    // Reject the sale exactly as before, but show the actual
                    // package conversion and quantity rather than an ambiguous
                    // "200 available" with no indication of the requested units.
                    var wholePackages = available > 0 ? available / unit.FactorMilli : 0;
                    throw new InvalidOperationException(
                        "الرصيد لا يكفي لإضافة الصنف «" + product.Name + "».\n"
                        + "الوحدة المختارة: " + unit.Name + " = "
                        + Amounts.Qty(unit.FactorMilli) + " " + product.Unit + ".\n"
                        + "الكمية المطلوبة: " + Amounts.Qty(entered) + " " + unit.Name
                        + " = " + Amounts.Qty(qty) + " " + product.Unit + ".\n"
                        + "المتاح بعد سطور الفاتورة: " + Amounts.Qty(available)
                        + " " + product.Unit + ".\n"
                        + "العبوات الكاملة الممكنة: " + wholePackages + " " + unit.Name
                        + ". جرّب عددًا أقل أو اختر الوحدة الأساسية.");
                }
            }
            var cart = purchase ? PurchaseLines : SaleLines;
            var editing = purchase ? _editingPurchaseLine : _editingSaleLine;
            if (editing == null && cart.Count >= 500)
                throw new ArgumentException("الفاتورة لا تقبل أكثر من 500 سطر.");
            var draft = new DraftLine { ProductId = product.Id, ProductName = product.Name,
                QuantityMilli = qty, PriceCents = price, PriceLevel=purchase ? null : SalePriceTier,
                ProductUnitId=unit.Id, UnitName=unit.Name,
                UnitFactorMilli=unit.FactorMilli,EnteredQuantityMilli=entered };
            // Validate the whole total before mutating the cart or notifying WPF.
            var other = cart.Where(x => !ReferenceEquals(x, editing)).ToList();
            Total(other.Concat(new[] { draft }));
            if (editing == null) cart.Add(draft);
            else
            {
                int index = cart.IndexOf(editing);
                if (index < 0) throw new InvalidOperationException("السطر المحدد لم يعد موجودًا.");
                cart[index] = draft;
                CancelEditLine(purchase);
            }
            TotalsChanged(); RaiseSaleAvailability();
        }

        private async Task PostAsync(bool purchase)
        {
            if (_posting) return;
            var cart = purchase ? PurchaseLines : SaleLines;
            if (cart.Count == 0) throw new ArgumentException("أضف صنفًا واحدًا على الأقل قبل الاعتماد.");
            var total = Total(cart);
            string title = purchase ? "مراجعة فاتورة الشراء" : "مراجعة فاتورة البيع";
            string message = "راجع البيانات قبل الاعتماد:\nعدد السطور: " + cart.Count
                + "\nالإجمالي: " + Amounts.Price(total) + " جنيه"
                + "\n\nبعد التأكيد سيتم تسجيل الفاتورة وحركة المخزون ولا يمكن تعديلهما مباشرة."
                + (purchase?"\nتُسجل فاتورة الشراء نقدية بعد تفعيل الحسابات.":"\nالمدفوع: "+Amounts.Price(CreditSale?Amounts.ToCents(SalePaid,"المدفوع"):total)+" جنيه. الباقي على العميل المحدد بعد تفعيل الحسابات.");
            if (MessageBox.Show(message, title, MessageBoxButton.YesNo,
                    MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _posting = true;
            try
            {
                long? supplier = purchase && SelectedSupplier != null ? (long?)SelectedSupplier.Id : null;
                var lines = cart.ToList();
                long? customer=!purchase && SaleCustomer!=null?(long?)SaleCustomer.Id:null;
                long? paid=!purchase && CreditSale?(long?)Amounts.ToCents(SalePaid,"المدفوع"):null;
                string code = await Task.Run(() => _service.Post(purchase, supplier, lines,customer,paid));
                cart.Clear();
                if(!purchase){CreditSale=false;SalePaid="0";SaleCustomer=null;}
                CancelEditLine(purchase);
                TotalsChanged();
                await ReloadAsync();
                if (!purchase)
                {
                    var handler = SalePosted;
                    if (handler != null) handler(code);
                }
                else Inform("تم اعتماد فاتورة الشراء رقم " + code + ".");
            }
            finally { _posting = false; }
        }

        private sealed class RefreshData
        {
            public List<ProductRow> Products;
            public List<PartyRow> Customers;
            public List<PartyRow> Suppliers;
            public List<DocumentRow> Documents;
            public List<MovementRow> Movements;
            public List<LowStockRow> LowStock;
        }
        private RefreshData ReadRefreshData()
        {
            return new RefreshData { Products=_service.Products(),
                Customers=CanSale ? _service.Customers() : new List<PartyRow>(),
                Suppliers=CanPurchase ? _service.Suppliers() : new List<PartyRow>(),
                Documents=CanReports ? _service.Documents() : new List<DocumentRow>(),
                Movements=CanReports ? _service.Movements() : new List<MovementRow>(),
                LowStock=_service.LowStock() };
        }
        private void Reload() { ApplyRefresh(ReadRefreshData()); }
        private async Task ReloadAsync() { ApplyRefresh(await Task.Run(ReadRefreshData)); }
        private void ApplyRefresh(RefreshData data)
        {
            var customerId=SaleCustomer==null?0L:SaleCustomer.Id;
            var supplierId = SelectedSupplier == null ? 0L : SelectedSupplier.Id;
            var purchaseProductId = PurchaseProduct == null ? 0L : PurchaseProduct.Id;
            var catalogProductId = CatalogProduct == null ? 0L : CatalogProduct.Id;
            var chosenPurchaseUnit=PurchaseUnit==null?0L:PurchaseUnit.Id;
            var chosenSaleUnit=SaleUnit==null?0L:SaleUnit.Id;
            var chosenCatalogUnit=CatalogUnit==null?0L:CatalogUnit.Id;
            var saleProductId = SaleProduct == null ? 0L : SaleProduct.Id;
            Replace(Products, data.Products);
            Replace(Suppliers, data.Suppliers);
            Replace(Customers,data.Customers);
            SaleCustomer=Customers.FirstOrDefault(x=>x.Id==customerId);
            Replace(Documents, data.Documents);
            Replace(Movements, data.Movements);
            Replace(LowStock,data.LowStock);
            Raise(nameof(LowStockCount)); Raise(nameof(LowStockBanner)); RaiseSaleAvailability();
            SelectedSupplier = Suppliers.FirstOrDefault(p => p.Id == supplierId) ?? Suppliers.FirstOrDefault();
            PurchaseProduct = Products.FirstOrDefault(p => p.Id == purchaseProductId) ?? Products.FirstOrDefault();
            SaleProduct = Products.FirstOrDefault(p => p.Id == saleProductId) ?? Products.FirstOrDefault();
            CatalogProduct = Products.FirstOrDefault(p=>p.Id==catalogProductId) ?? Products.FirstOrDefault();
            PurchaseUnit = PurchaseUnits.FirstOrDefault(x=>x.Id==chosenPurchaseUnit) ?? PurchaseUnits.FirstOrDefault();
            SaleUnit = SaleUnits.FirstOrDefault(x=>x.Id==chosenSaleUnit) ?? SaleUnits.FirstOrDefault();
            CatalogUnit = CatalogUnits.FirstOrDefault(x=>x.Id==chosenCatalogUnit) ?? CatalogUnits.FirstOrDefault();
            Raise(nameof(ProductCount)); Raise(nameof(SupplierCount)); Raise(nameof(DatabasePath));
        }
        private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
        {
            target.Clear();
            foreach (var item in items) target.Add(item);
        }
    }
}
