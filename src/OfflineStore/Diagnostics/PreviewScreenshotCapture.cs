#if SCREENSHOT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OfflineStore.Models;
using OfflineStore.Infrastructure;
using OfflineStore.Security;
using OfflineStore.Supply;
using OfflineStore.Services;
using OfflineStore.ViewModels;

namespace OfflineStore
{
    // CI-only visual evidence: captures real WPF visual trees with disposable demo data.
    // This class is not compiled into production or installer builds.
    internal static class PreviewScreenshotCapture
    {
        private static DraftLine Line(StoreService s, long id, string unitName, int count, long price)
        {
            var p = s.Products().Single(x => x.Id == id);
            var unit = s.Units(id).Single(x => x.Name == unitName);
            long entered = count * 1000L;
            return new DraftLine {
                ProductId=id, ProductName=p.Name, ProductUnitId=unit.Id,
                UnitName=unit.Name, UnitFactorMilli=unit.FactorMilli,
                EnteredQuantityMilli=entered,
                QuantityMilli=Amounts.BaseQuantity(entered,unit.FactorMilli),
                PriceCents=price
            };
        }

        private static void Seed(StoreService s)
        {
            s.AddSupplier("الشركة المتحدة للتوريدات", "01000000000");
            s.AddSupplier("تجارة النور", "01000000001");
            s.AddProduct("TEA-250", "شاي سادة 250 جم", "قطعة", 1500, 1050, 10000, "6221000000101");
            s.AddProduct("OIL-1L", "زيت طعام 1 لتر", "قطعة", 8500, 6400, 6000, "6221000000102");
            s.AddProduct("SUG-1KG", "سكر أبيض 1 كجم", "قطعة", 3600, 2850, 10000, "6221000000103");
            s.AddProduct("RICE-1KG", "أرز أبيض 1 كجم", "قطعة", 4200, 3400, 10000, "6221000000104");
            s.AddProduct("TISSUE", "مناديل ورقية", "علبة", 2500, 1800, 7000, "6221000000105");

            var items = s.Products();
            long tea=items.Single(x=>x.Sku=="TEA-250").Id;
            long oil=items.Single(x=>x.Sku=="OIL-1L").Id;
            long sug=items.Single(x=>x.Sku=="SUG-1KG").Id;
            long rice=items.Single(x=>x.Sku=="RICE-1KG").Id;
            s.AddUnit(tea,"دستة",12000,17000,"6221000000201");
            s.AddUnit(tea,"كرتونة",48000,60000,"6221000000301");
            s.AddUnit(oil,"كرتونة",12000,94000,"6221000000302");
            s.AddUnit(sug,"كرتونة",20000,68000,"6221000000303");
            s.AddUnit(rice,"شيكارة",25000,97000,"6221000000304");

            var supplier = s.Suppliers().First().Id;
            s.Post(true, supplier, new List<DraftLine> {
                Line(s,tea,"كرتونة",2,49000),
                Line(s,oil,"كرتونة",3,77000),
                Line(s,sug,"كرتونة",2,57000),
                Line(s,rice,"شيكارة",2,78000)
            });
            s.Post(false, null, new List<DraftLine> {
                Line(s,tea,"قطعة",4,1500),
                Line(s,oil,"قطعة",2,8500),
                Line(s,sug,"قطعة",3,3600)
            });
        }

        private static void Save(FrameworkElement surface, string path)
        {
            surface.UpdateLayout();
            Application.Current.Dispatcher.Invoke(
                DispatcherPriority.Render, new Action(() => { }));
            int width = Math.Max(1000,(int)Math.Ceiling(surface.ActualWidth));
            int height = Math.Max(650,(int)Math.Ceiling(surface.ActualHeight));
            var bitmap = new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
            bitmap.Render(surface);
            // Rendering an inherited RTL WPF root directly mirrors the captured
            // pixels; flip the TEST-ONLY bitmap so Arabic screenshots are legible.
            BitmapSource readable = surface.FlowDirection == FlowDirection.RightToLeft ? (BitmapSource)new TransformedBitmap(bitmap, new ScaleTransform(-1, 1)) : bitmap;
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(readable));
            using(var output=File.Create(path)) encoder.Save(output);
            if(new FileInfo(path).Length < 12000)
                throw new InvalidOperationException("Capture is unexpectedly small: " + path);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr FindWindow(string kind,string title);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr handle,uint message,IntPtr wParam,IntPtr lParam);

        private static void VerifyOperationalUi(MainWindow view, StoreService service)
        {
            var vm=(MainViewModel)view.DataContext;
            var confirm=typeof(MainWindow).GetMethod("ConfirmInvoiceChanges",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            if(!(bool)confirm.Invoke(view,null)) throw new InvalidOperationException("QA: empty invoice prompts at close.");
            foreach(var cart in new [] {vm.SaleLines,vm.PurchaseLines})
            {
                cart.Add(new DraftLine());
                bool answered=false;
                var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
                timer.Tick+=(sender,args)=> {
                    var dialog=FindWindow("#32770","فاتورة غير محفوظة");
                    if(dialog!=IntPtr.Zero) { answered=true; PostMessage(dialog,0x0111,new IntPtr(7),IntPtr.Zero); }
                };
                timer.Start();
                try
                {
                    if((bool)confirm.Invoke(view,null) || !answered)
                        throw new InvalidOperationException("QA: No did not preserve an unposted invoice at close.");
                }
                finally {timer.Stop();cart.Clear();}
            }
            var product=vm.Products.First();
            var unit=service.Units(product.Id).First();
            long before=product.StockMilli;
            var draft=new SupplyVoucherDraft {SupplierID=service.Suppliers().First().Id,PaymentType="CREDIT",
                Lines=new List<SupplyVoucherLine> {new SupplyVoucherLine {ProductID=product.Id,
                    ProductUnitID=unit.Id,Unit=unit.Name,UnitFactorMilli=unit.FactorMilli,
                    QuantityMilli=1000,UnitCostCents=300}}};
            var saved=service.SupplyVouchers.SaveDraft(draft);
            service.SupplyVouchers.Approve(saved.VoucherID);
            var panel=(SupplyVoucherControl)view.FindName("GoodsReceiptPanel");
            var posted=typeof(SupplyVoucherControl).GetField("Posted",
                System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic).GetValue(panel) as Action;
            if(posted==null) throw new InvalidOperationException("QA: goods receipt refresh event is detached.");
            posted();
            var frame=new DispatcherFrame(); var limit=DateTime.UtcNow.AddSeconds(15);
            var poll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            poll.Tick+=(sender,args)=> {if(!vm.IsBusy || DateTime.UtcNow>limit)frame.Continue=false;};
            poll.Start();
            try {Dispatcher.PushFrame(frame);} finally {poll.Stop();}
            if(vm.IsBusy || vm.Products.Single(p=>p.Id==product.Id).StockMilli!=before+unit.FactorMilli)
                throw new InvalidOperationException("QA: received stock did not refresh without F5.");
        }

        private static void VerifyFinanceUi(MainWindow owner,StoreService service,string folder)
        {
            var window=new FinanceWindow(service) {Owner=owner};window.Show();
            var frame=new DispatcherFrame();var limit=DateTime.UtcNow.AddSeconds(20);
            var poll=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(25)};
            poll.Tick+=(s,e)=>{if(window.IsEnabled || DateTime.UtcNow>limit)frame.Continue=false;};
            poll.Start();try{Dispatcher.PushFrame(frame);}finally{poll.Stop();}
            var grid=(DataGrid)window.FindName("Balances");
            var status=(TextBlock)window.FindName("Status");
            if(!window.IsEnabled || grid.ItemsSource==null || !status.Text.Contains("غير مفعلة"))
                throw new InvalidOperationException("QA: accounting screen did not load real ledger data.");
            var pending=(TextBox)window.FindName("ReturnMemo");pending.Text="مرتجع لم يعتمد";
            var confirm=typeof(FinanceWindow).GetMethod("ConfirmPendingFinanceChanges",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic);
            bool answered=false;var answer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(50)};
            answer.Tick+=(sender,args)=>{var dialog=FindWindow("#32770","مدخلات مالية غير معتمدة");if(dialog!=IntPtr.Zero){answered=true;PostMessage(dialog,0x0111,new IntPtr(7),IntPtr.Zero);}};
            answer.Start();try{if((bool)confirm.Invoke(window,null) || !answered)throw new InvalidOperationException("QA: unsaved return was discarded despite No.");}finally{answer.Stop();}
            pending.Clear();if(!(bool)confirm.Invoke(window,null))throw new InvalidOperationException("QA: clean finance screen prompts at close.");
            Save(window,Path.Combine(folder,"17-accounting-ledger.png"));window.Close();
        }

        public static void Run()
        {
            try
            {
                var directory = Environment.GetEnvironmentVariable("OFFLINESTORE_CAPTURE_DIR");
                if(string.IsNullOrWhiteSpace(directory))
                    throw new InvalidOperationException("Specify an output directory.");
                Directory.CreateDirectory(directory);

                var service = new StoreService();
                service.Initialize();
                if (!service.Users.HasUsers())
                {
                    // The original automated screenshots skipped login altogether.
                    // Render and assert the actual first-run dialog before creating users.
                    var first = new UserLoginWindow(service.Users)
                    {
                        Width = 520, Height = 610,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 0, Top = 0
                    };
                    first.Show();
                    first.UpdateLayout();
                    var header = first.FindName("PageTitle") as TextBlock;
                    var submit = first.FindName("SubmitButton") as Button;
                    if (header == null || submit == null)
                        throw new InvalidOperationException("QA: first-run controls missing.");
                    var top = header.TransformToAncestor(first).Transform(new Point(0, 0));
                    var bottom = submit.TransformToAncestor(first).Transform(new Point(0, 0));
                    if (top.Y < 0 || bottom.Y < 0 ||
                        bottom.Y + submit.ActualHeight > first.ActualHeight)
                        throw new InvalidOperationException("QA: first-run title or action button is clipped.");
                    var loginText=first.FindName("LoginName") as TextBox;
                    loginText.Text="admin-123";
                    loginText.Focus(); first.UpdateLayout();
                    var firstGlyph=loginText.GetRectFromCharacterIndex(0);
                    var lastGlyph=loginText.GetRectFromCharacterIndex(loginText.Text.Length-1,true);
                    var foreground=loginText.Foreground as SolidColorBrush;
                    var background=loginText.Background as SolidColorBrush;
                    if(firstGlyph.IsEmpty || firstGlyph.Height<=0 || lastGlyph.X<=firstGlyph.X
                        || foreground==null || background==null || foreground.Color==background.Color)
                        throw new InvalidOperationException("QA: login text is clipped or invisible.");
                    loginText.Text="admin";
                    Save((FrameworkElement)first.Content, Path.Combine(directory,"00-first-run-login.png"));
                    first.Close();

                    bool emptyRejected = false;
                    try { service.Users.CreateFirstAdmin("مدير المحل", ""); }
                    catch (ArgumentException) { emptyRejected = true; }
                    if (!emptyRejected || service.Users.HasUsers())
                        throw new InvalidOperationException("QA: empty first-admin PIN accepted.");
                    service.Users.CreateFirstAdmin("مدير المحل - عرض تجريبي", "92837465");
                    // Only the isolated screenshot database is modified by this fixture.
                    long originalAdmin = service.Users.Login("admin", "92837465").Id;
                    using (var c = new OfflineStore.Infrastructure.StoreDb().Open())
                    using (var find = new System.Data.SQLite.SQLiteCommand("SELECT salt,iterations FROM users WHERE id=@id", c))
                    {
                        find.Parameters.AddWithValue("@id", originalAdmin);
                        byte[] salt; int cycles;
                        using (var r = find.ExecuteReader()) { r.Read(); salt=(byte[])r[0]; cycles=r.GetInt32(1); }
                        using (var derive = new System.Security.Cryptography.Rfc2898DeriveBytes("", salt, cycles,
                            System.Security.Cryptography.HashAlgorithmName.SHA256))
                        using (var update = new System.Data.SQLite.SQLiteCommand("UPDATE users SET password_hash=@hash WHERE id=@id",c))
                        {
                            update.Parameters.AddWithValue("@hash", derive.GetBytes(32));
                            update.Parameters.AddWithValue("@id", originalAdmin); update.ExecuteNonQuery();
                        }
                    }
                    bool setupRequired = false;
                    try { service.Users.Login("admin", ""); }
                    catch (LegacyAdminPinRequiredException) { setupRequired = true; }
                    if (!setupRequired) throw new InvalidOperationException("QA: legacy empty admin obtained session.");
                    service.Users.ProtectLegacyAdmin("admin", "92837465");
                    if (service.Users.Login("admin", "92837465").Id != originalAdmin)
                        throw new InvalidOperationException("QA: legacy account identity changed.");
                    if (service.Users.CanAutoLoginAdmin())
                        throw new InvalidOperationException("QA: administrative auto-login enabled.");

                }
                service.SignIn(service.Users.Login("admin", "92837465"));
                Seed(service);
                // Reproduce the reported 200-base-unit vs two packages case
                // with the REAL WPF ViewModel and transactional SQLite service.
                var stockCheck = new MainViewModel(service);
                var stockOil = stockCheck.Products.Single(x => x.Sku == "OIL-1L");
                long oilBefore = stockOil.StockMilli;
                if (oilBefore != 34000)
                    throw new InvalidOperationException("QA: expected 34 single bottles after sale.");
                stockCheck.SaleProduct = stockOil;
                stockCheck.SaleUnit = stockCheck.SaleUnits.Single(x => x.Name == "كرتونة");
                stockCheck.SaleQty = "٣"; // Arabic-Indic digits typed by cashier.
                if (!stockCheck.IsSaleQuantityInsufficient ||
                    !stockCheck.SaleQuantityPreview.Contains(Amounts.Qty(36000)) ||
                    !stockCheck.SaleQuantityPreview.Contains(Amounts.Qty(34000)))
                    throw new InvalidOperationException("QA: live invoice unit conversion is misleading.");
                bool shortageBlocked = false;
                try { stockCheck.AddSaleLineCommand.Execute(null); }
                catch (InvalidOperationException ex)
                {
                    shortageBlocked = ex.Message.Contains(Amounts.Qty(36000))
                        && ex.Message.Contains(Amounts.Qty(34000))
                        && ex.Message.Contains("كرتونة");
                }
                if (!shortageBlocked || stockCheck.SaleLines.Count != 0)
                    throw new InvalidOperationException(
                        "QA: insufficient packages were accepted or error omitted actual figures.");
                stockCheck.SaleQty = "٢";
                if (stockCheck.IsSaleQuantityInsufficient)
                    throw new InvalidOperationException("QA: two 12-piece cartons should fit 34 bottles.");
                stockCheck.AddSaleLineCommand.Execute(null);
                if (stockCheck.SaleLines.Count != 1 ||
                    stockCheck.SaleLines[0].QuantityMilli != 24000 ||
                    service.Products().Single(x => x.Sku == "OIL-1L").StockMilli != oilBefore)
                    throw new InvalidOperationException(
                        "QA: draft line improperly changed inventory or converted packs incorrectly.");
                using (var precisionDb = new OfflineStore.Infrastructure.StoreDb().Open())
                using (var precision = new System.Data.SQLite.SQLiteCommand(@"SELECT c.average_cost_millicents,
                    c.total_cost_cents,l.qty_milli,e.qty_before,e.value_before,e.group_qty,e.group_offset FROM sale_cost_snapshots c
                    JOIN document_lines l ON l.id=c.document_line_id LEFT JOIN inventory_cost_evidence e ON e.sale_line_id=l.id",precisionDb))
                using (var r=precision.ExecuteReader())
                {
                    bool any=false;
                    while(r.Read()) { any=true; long expected=r.IsDBNull(3)?Amounts.CostTotal(r.GetInt64(2),r.GetInt64(0)):Amounts.ProportionalReturn(Amounts.ProportionalReturn(r.GetInt64(4),0,r.GetInt64(5),r.GetInt64(3)),r.GetInt64(6),r.GetInt64(2),r.GetInt64(5));if(r.GetInt64(1)!=expected)
                        throw new InvalidOperationException("QA: precise sale cost mismatch."); }
                    if(!any) throw new InvalidOperationException("QA: precise sale cost snapshots missing.");
                }
                // Profit snapshots must use the moving-average cost basis,
                // not the last quoted purchase price.
                using (var checkDb = new StoreDb().Open())
                using (var check = new System.Data.SQLite.SQLiteCommand(@"
                    SELECT l.unit_cost_snapshot_cents,c.average_cost_millicents
                    FROM document_lines l
                    JOIN documents d ON d.id=l.document_id
                    JOIN products p ON p.id=l.product_id JOIN sale_cost_snapshots c ON c.document_line_id=l.id
                    WHERE d.doc_type='SALE' AND d.state='POSTED' AND p.sku='TEA-250'
                    LIMIT 1", checkDb))
                using (var reader = check.ExecuteReader())
                {
                    if (!reader.Read())
                        throw new InvalidOperationException("QA: sale cost snapshot missing.");
                    long snapshot = reader.GetInt64(0), avg = reader.GetInt64(1);
                    if (snapshot != (long)decimal.Round(avg/1000m, 0,
                            MidpointRounding.AwayFromZero))
                        throw new InvalidOperationException(
                            "QA: sale snapshot uses last supplier cost instead of weighted average.");
                }

                // Exercise catalog edit and supplier creation on the REAL C#/SQLite service.
                var before = service.Products().Single(p => p.Sku == "TEA-250");
                long stockBeforeEdit = before.StockMilli;
                long avgBeforeEdit;
                using (var costDb = new StoreDb().Open())
                using (var costCmd = new System.Data.SQLite.SQLiteCommand(
                    "SELECT avg_cost_millicents FROM products WHERE id=@id", costDb))
                {
                    costCmd.Parameters.AddWithValue("@id", before.Id);
                    avgBeforeEdit = Convert.ToInt64(costCmd.ExecuteScalar());
                }
                // TEA was purchased as cartons at a price that produces a
                // fractional cent per base unit. Verify this regression is meaningful.
                if (stockBeforeEdit <= 0 || avgBeforeEdit % 1000 == 0)
                    throw new InvalidOperationException("QA: missing fractional moving-average fixture.");
                service.UpdateProduct(before.Id, "TEA-250", "شاي سادة ممتاز 250 جم",
                    1600, before.LastCostCents, 10000, before.Barcode, "تجارة النور");
                var edited = service.Products().Single(p => p.Sku == "TEA-250");
                if (edited.Name != "شاي سادة ممتاز 250 جم" || edited.SalePriceCents != 1600
                    || edited.PreferredSupplier != "تجارة النور" || edited.StockMilli != stockBeforeEdit)
                    throw new InvalidOperationException("QA: catalog edit changed stock or failed to persist.");
                using (var costDb = new StoreDb().Open())
                using (var costCmd = new System.Data.SQLite.SQLiteCommand(
                    "SELECT avg_cost_millicents FROM products WHERE id=@id", costDb))
                {
                    costCmd.Parameters.AddWithValue("@id", before.Id);
                    if (Convert.ToInt64(costCmd.ExecuteScalar()) != avgBeforeEdit)
                        throw new InvalidOperationException(
                            "QA: catalog edit destroyed fractional moving-average valuation.");
                }
                service.AddProduct("NEW-01", "صنف بمورد جديد", "قطعة", 500, 250, 1000,
                    "6221000000988", "مورد يتسجل أول مرة");
                if (service.Products().Single(p => p.Sku == "NEW-01").PreferredSupplier != "مورد يتسجل أول مرة"
                    || !service.Suppliers().Any(p => p.Name == "مورد يتسجل أول مرة"))
                    throw new InvalidOperationException("QA: inline supplier was not created and linked.");
                if (service.Products().Single(p => p.Sku == "NEW-01").StockMilli != 0)
                    throw new InvalidOperationException("QA: supplier association created phantom stock.");

                // Real SQLite/C# integration check for session invalidation.
                // Uses only disposable CI demo data and never the installer build.
                var users = service.Users;
                // Exact PIN policy boundaries: 6 and 12 digits accepted;
                // 5 and 13 digits rejected before any account is created.
                users.CreateUser(service.CurrentUser, "pin6_qa", "موظف ستة أرقام",
                    "CASHIER", "123456");
                users.CreateUser(service.CurrentUser, "pin12_qa", "موظف اثنا عشر رقم",
                    "CASHIER", "123456789012");
                if (users.Login("pin6_qa", "123456").Id <= 0 ||
                    users.Login("pin12_qa", "123456789012").Id <= 0)
                    throw new InvalidOperationException("QA: PIN minimum/maximum login failed.");
                foreach (var invalidPin in new[] { "12345", "1234567890123", "ab1234" })
                {
                    bool rejected = false;
                    try
                    {
                        users.CreateUser(service.CurrentUser,
                            "invalid_"+invalidPin.Length, "موظف غير صالح",
                            "CASHIER", invalidPin);
                    }
                    catch (ArgumentException) { rejected = true; }
                    if (!rejected)
                        throw new InvalidOperationException("QA: invalid PIN was accepted.");
                }
                users.CreateUser(service.CurrentUser, "cashier_qa", "موظف اختبار", "CASHIER", "83915472");
                var previousSession = users.Login("cashier_qa", "83915472");
                if (!users.Authorize(previousSession, "SALE"))
                    throw new InvalidOperationException("QA: cashier session was not authorized.");
                users.ResetPassword(service.CurrentUser, previousSession.Id, "47283915");
                if (users.Authorize(previousSession, "SALE"))
                    throw new InvalidOperationException("QA: password reset did not invalidate old session.");
                var newSession = users.Login("cashier_qa", "47283915");
                if (!users.Authorize(newSession, "SALE") || users.Authorize(newSession, "PURCHASE"))
                    throw new InvalidOperationException("QA: role permissions are incorrect.");

                // Exercise custom permissions on REAL C#/SQLite authorization.
                // Default roles remain intact, but a new cashier can also see reports.
                var adminSession=service.CurrentUser;
                users.CreateUserWithPermissions(adminSession, "permissions_qa",
                    "موظف بصلاحيات محددة", "CASHIER", "18473629",
                    new [] { "SALE", "REPORTS" });
                var customSession=users.Login("permissions_qa", "18473629");
                if (!users.Authorize(customSession, "SALE") ||
                    !users.Authorize(customSession, "REPORTS") ||
                    users.Authorize(customSession, "PURCHASE") ||
                    users.Authorize(customSession, "BACKUP") ||
                    users.Authorize(customSession, "USERS"))
                    throw new InvalidOperationException("QA: custom employee grants too many privileges.");
                users.SetUserPermissions(adminSession, customSession.Id,
                    new [] { "PURCHASE", "LOW_STOCK" });
                // The SAME already-logged-in session must lose SALE immediately.
                if (users.Authorize(customSession, "SALE") ||
                    users.Authorize(customSession, "REPORTS") ||
                    !users.Authorize(customSession, "PURCHASE") ||
                    !users.Authorize(customSession, "LOW_STOCK") ||
                    users.Authorize(customSession, "USERS"))
                    throw new InvalidOperationException("QA: permission revocation was not immediate.");
                bool deniedPermissionAdmin=false;
                try { users.SetUserPermissions(customSession, newSession.Id,
                    new [] { "BACKUP" }); }
                catch (UnauthorizedAccessException) { deniedPermissionAdmin=true; }
                if (!deniedPermissionAdmin)
                    throw new InvalidOperationException("QA: employee changed another user's permissions.");
                bool blockedAdminCustom=false;
                try { users.SetUserPermissions(adminSession, adminSession.Id,
                    new [] { "SALE" }); }
                catch (InvalidOperationException) { blockedAdminCustom=true; }
                if (!blockedAdminCustom)
                    throw new InvalidOperationException("QA: administrative account restrictions were bypassed.");

                // Real Windows DPAPI round trip for the opt-in saved employee PIN.
                RememberedLoginStore.Save("cashier_qa", "47283915");
                var rememberedEmployee = RememberedLoginStore.TryLoad();
                if (rememberedEmployee == null ||
                    rememberedEmployee.Username != "cashier_qa" ||
                    rememberedEmployee.Password != "47283915")
                    throw new InvalidOperationException("QA: DPAPI saved employee login failed.");
                var credentialFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "OfflineStore", "remembered-login.dat");
                var encryptedBytes = File.ReadAllBytes(credentialFile);
                if (System.Text.Encoding.UTF8.GetString(encryptedBytes).Contains("47283915"))
                    throw new InvalidOperationException("QA: remembered PIN stored in plain text.");
                RememberedLoginStore.Forget();
                if (RememberedLoginStore.TryLoad() != null)
                    throw new InvalidOperationException("QA: forgetting saved employee did not work.");

                // Real DB tests for unit-level retail / semi-retail / wholesale.
                var teaForTiers = service.Products().Single(p => p.Sku == "TEA-250").Id;
                var teaUnit = service.Units(teaForTiers).Single(u => u.Name == "قطعة");
                service.SetAllUnitPrices(teaUnit.Id, 1500, 1400, 1200);
                teaUnit = service.Units(teaForTiers).Single(u => u.Name == "قطعة");
                if (teaUnit.SalePriceCents != 1500 ||
                    teaUnit.SemiPriceCents != 1400 ||
                    teaUnit.WholesalePriceCents != 1200)
                    throw new InvalidOperationException("QA: price tiers did not persist.");
                var wholesaleLine = Line(service, teaForTiers, "قطعة", 1, 1200);
                wholesaleLine.PriceLevel = "WHOLESALE";
                var wholesaleSale = service.Post(false, null, new List<DraftLine> { wholesaleLine });
                using (var checkDb = new StoreDb().Open())
                using (var snapshot = new System.Data.SQLite.SQLiteCommand(@"SELECT dl.sale_price_level
                    FROM document_lines dl JOIN documents d ON d.id=dl.document_id
                    WHERE d.doc_code=@code", checkDb))
                {
                    snapshot.Parameters.AddWithValue("@code", wholesaleSale);
                    if (!string.Equals(snapshot.ExecuteScalar() as string, "WHOLESALE",
                            StringComparison.Ordinal))
                        throw new InvalidOperationException("QA: posted invoice did not snapshot price tier.");
                }
                var cashierShop = new StoreService();
                cashierShop.Initialize();
                cashierShop.SignIn(newSession);
                var forgedWholesale = Line(cashierShop, teaForTiers, "قطعة", 1, 1000);
                forgedWholesale.PriceLevel = "WHOLESALE";
                bool cashierPriceRejected = false;
                try { cashierShop.Post(false, null, new List<DraftLine> { forgedWholesale }); }
                catch (UnauthorizedAccessException) { cashierPriceRejected = true; }
                if (!cashierPriceRejected)
                    throw new InvalidOperationException("QA: cashier bypassed wholesale price protection.");

                var sample = @"{\rtf1\ansi\deff0\pard Test note with \b bold\b0 text.\par}";
                var noteId = service.Notes.Create("متابعة المخزون", sample);
                if (!service.Notes.ListAll().Any(n => n.Id == noteId))
                    throw new InvalidOperationException("QA: note did not save.");
                service.Notes.Update(noteId, "متابعة المخزون - تعديل", sample);
                if (!service.Notes.ListAll().Any(n => n.Id == noteId && n.Title.Contains("تعديل")))
                    throw new InvalidOperationException("QA: note update did not persist.");
                var cashierNotes = new EmployeeNotesService(new StoreDb(), newSession);
                bool blocked = false;
                try { cashierNotes.Update(noteId, "لا ينبغي حفظه", sample); }
                catch (UnauthorizedAccessException) { blocked = true; }
                if (!blocked) throw new InvalidOperationException("QA: cashier edited another user's note.");
                var employeeNote = cashierNotes.Create("ملاحظات الكاشير", sample);
                cashierNotes.Update(employeeNote, "ملاحظات الكاشير - متابعة", sample);

                // Real C#/SQLite end-to-end receipt: save draft -> no stock -> post -> one credit.
                var suppliedProduct=service.Products().Single(x=>x.Sku=="NEW-01");
                var suppliedUnit=service.Units(suppliedProduct.Id).Single(x=>x.Name=="قطعة");
                var goods=new SupplyVoucherDraft {
                    SupplierID=service.Suppliers().First().Id,
                    VoucherDate=DateTime.Today,
                    SupplierInvoiceDate=DateTime.Today,
                    SupplierInvoiceNo="QA-SUPPLY-0001",
                    PaymentType="CREDIT",
                    PaidAmountCents=200,
                    TaxRateBps=1400,
                    Lines=new List<SupplyVoucherLine> {
                        new SupplyVoucherLine {
                            ProductID=suppliedProduct.Id,ProductUnitID=suppliedUnit.Id,
                            ItemCode=suppliedProduct.Sku,ItemName=suppliedProduct.Name,Unit=suppliedUnit.Name,
                            UnitFactorMilli=suppliedUnit.FactorMilli,QuantityMilli=2000,
                            UnitCostCents=500,DiscountType="AMOUNT",DiscountValue=0
                        }
                    }
                };
                long beforeSupply=service.Products().Single(x=>x.Id==suppliedProduct.Id).StockMilli;
                var draftSupply=service.SupplyVouchers.SaveDraft(goods);
                if (!draftSupply.VoucherCode.StartsWith("ROF-") ||
                    service.Products().Single(x=>x.Id==suppliedProduct.Id).StockMilli!=beforeSupply)
                    throw new InvalidOperationException("QA: receipt draft changed stock or has invalid code.");
                var approved=service.SupplyVouchers.Approve(draftSupply.VoucherID);
                var afterSupply=service.Products().Single(x=>x.Id==suppliedProduct.Id);
                if (afterSupply.StockMilli!=beforeSupply+2000 || afterSupply.LastCostCents!=570)
                    throw new InvalidOperationException("QA: receipt stock / moving average incorrect.");
                if (!service.SupplyVouchers.Search(approved.VoucherCode).Any(x=>x.VoucherID==approved.VoucherID))
                    throw new InvalidOperationException("QA: receipt lookup failed.");
                if (!service.Documents().Any(x=>x.Code.StartsWith("P-") && x.TotalCents==1140))
                    throw new InvalidOperationException("QA: integrated invoice total ignores VAT.");
                bool duplicateBlocked=false;
                try { service.SupplyVouchers.Approve(draftSupply.VoucherID); }
                catch (InvalidOperationException) { duplicateBlocked=true; }
                if (!duplicateBlocked)
                    throw new InvalidOperationException("QA: duplicate receipt approval succeeded.");
                var deletion=new SupplyVoucherDraft {
                    SupplierID=service.Suppliers().First().Id,
                    VoucherDate=DateTime.Today,SupplierInvoiceDate=DateTime.Today,
                    PaymentType="CASH",
                    Lines=new List<SupplyVoucherLine> {
                        new SupplyVoucherLine {
                            ProductID=suppliedProduct.Id,ProductUnitID=suppliedUnit.Id,
                            Unit=suppliedUnit.Name,UnitFactorMilli=1000,QuantityMilli=1000,
                            UnitCostCents=300,DiscountType="AMOUNT",DiscountValue=0
                        }
                    }
                };
                var toDelete=service.SupplyVouchers.SaveDraft(deletion);
                service.SupplyVouchers.DeleteDraft(toDelete.VoucherID);
                if (service.Products().Single(x=>x.Id==suppliedProduct.Id).StockMilli!=beforeSupply+2000)
                    throw new InvalidOperationException("QA: deleting draft changed stock.");

                // Reopen/revalidate the persistent database as the NEXT Windows
                // app startup would. Catches problems missed by current-transaction tests.
                new StoreDb().Initialize();
                var reopened = service.SupplyVouchers.Load(approved.VoucherID);
                if (reopened.State != "POSTED" || reopened.Lines.Count != 1 ||
                    reopened.VoucherCode != approved.VoucherCode)
                    throw new InvalidOperationException("QA: posted supply did not survive database integrity check.");

                // End-to-end on the actual Windows SQLite provider:
                // snapshot -> mutate database -> queue restore -> startup swap -> verify.
                string backupPath = Path.Combine(directory, "qa-before-restore-" +
                    Guid.NewGuid().ToString("N") + ".db");
                service.BackupTo(backupPath);
                service.AddProduct("QA-RESTORE-PROBE", "سيُحذف بعد الاسترجاع",
                    "قطعة", 100, 60, 0, "6221000099901");
                if (!service.Products().Any(x=>x.Sku=="QA-RESTORE-PROBE"))
                    throw new InvalidOperationException("QA: restore mutation prerequisite missing.");
                service.StageDatabaseRestore(backupPath);
                new StoreDb().Initialize();
                if (service.Products().Any(x=>x.Sku=="QA-RESTORE-PROBE"))
                    throw new InvalidOperationException("QA: offline restore did not replace the database.");
                if (service.SupplyVouchers.Load(approved.VoucherID).State!="POSTED")
                    throw new InvalidOperationException("QA: restored backup lost posted receipt.");

                // Simulate a second volume on Windows CI (SUBST). It is NOT
                // a substitute for testing a real USB disk at the shop.
                string secondaryDrive = Environment.GetEnvironmentVariable(
                    "OFFLINESTORE_TEST_EXTERNAL_ROOT");
                if (!string.IsNullOrWhiteSpace(secondaryDrive))
                {
                    bool blockedSystemDrive = false;
                    try
                    {
                        service.ConfigureExternalBackupFolder(
                            Path.GetDirectoryName(service.DatabasePath));
                    }
                    catch (InvalidOperationException) { blockedSystemDrive = true; }
                    if (!blockedSystemDrive)
                        throw new InvalidOperationException("QA: backup was permitted on Windows drive.");
                    string externalFolder = service.ConfigureExternalBackupFolder(secondaryDrive);
                    string manual = service.CreateExternalBackup();
                    string automatic = service.CreateScheduledExternalBackupIfDue();
                    string redundant = service.CreateScheduledExternalBackupIfDue();
                    if (!File.Exists(manual) || !File.Exists(automatic) ||
                        redundant != null || !manual.StartsWith(externalFolder,
                            StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException(
                            "QA: verified external backup or schedule failed.");
                    new SqliteRecoveryService(new StoreDb()).VerifyBackupFile(manual);
                    new SqliteRecoveryService(new StoreDb()).VerifyBackupFile(automatic);
                }

                // Cashiers must still be able to start OfflineStore while receipt
                // controls remain inaccessible; unauthorized initialization may not crash.
                service.SignIn(newSession);
                if (service.Can("PURCHASE"))
                    throw new InvalidOperationException("QA: CASHIER incorrectly granted PURCHASE.");
                var cashierView=new MainWindow(service);
                // WPF inherited bindings are evaluated after the window enters its visual tree.
                // Testing IsEnabled before Show() reads its unbound default (true).
                cashierView.Show();
                cashierView.UpdateLayout();
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => { }));
                var cashierReceipt=cashierView.FindName("GoodsReceiptTab") as TabItem;
                if(cashierReceipt==null || cashierReceipt.IsEnabled)
                    throw new InvalidOperationException("QA: cashier receipt permissions invalid after WPF layout.");
                cashierView.Close();
                service.SignIn(users.Login("admin","92837465"));

                var view = new MainWindow(service) { Width=1390,Height=860,
                    WindowStartupLocation=WindowStartupLocation.Manual,
                    Left=0, Top=0 };
                Application.Current.MainWindow = view;
                Application.Current.ShutdownMode = ShutdownMode.OnMainWindowClose;
                view.Show();

                var tabs = view.FindName("MainTabs") as TabControl;
                if (tabs == null) throw new InvalidOperationException("MainTabs not found.");
                VerifyOperationalUi(view,service);
                VerifyFinanceUi(view,service,directory);
                var vm = (MainViewModel)view.DataContext;
                var product = vm.Products.Single(p=>p.Sku=="TEA-250");
                vm.SaleProduct = product;
                vm.SaleUnit = vm.SaleUnits.Single(u=>u.Name=="كرتونة");
                vm.SaleQty = "1";
                vm.AddSaleLineCommand.Execute(null);
                vm.SaleUnit = vm.SaleUnits.Single(u=>u.Name=="قطعة");
                vm.SaleQty = "3";
                vm.AddSaleLineCommand.Execute(null);
                // A draft edit replaces the selected line; no phantom third line
                // and no stock change before the user posts the sale.
                vm.SelectedSaleLine=vm.SaleLines.First();
                vm.EditSaleLineCommand.Execute(null);
                // The fixture has 92 remaining pieces after its first sale;
                // half a 48-piece carton is valid, two whole cartons are not.
                vm.SaleQty="0.5";
                vm.AddSaleLineCommand.Execute(null);
                if(vm.SaleLines.Count!=2 || vm.SaleLines.First().EnteredQuantityMilli!=500 ||
                   vm.IsEditingSaleLine)
                    throw new InvalidOperationException("QA: editing sale line duplicated or failed.");
                if (!((TabItem)tabs.Items[0]).IsSelected)
                    throw new InvalidOperationException("QA: application does not open on dashboard.");
                var catalog = vm.Products.Single(p => p.Sku == "TEA-250");
                vm.CatalogProduct = catalog;
                vm.StartEditProductCommand.Execute(null);
                if (!vm.IsEditingProduct || vm.NewName != catalog.Name
                    || vm.CatalogSupplierText != catalog.PreferredSupplier || vm.IsUnitEditable)
                    throw new InvalidOperationException("QA: edit selected product form did not populate.");

                var nameInput = view.FindName("ProductNameInput") as TextBox;
                var suggestions = view.FindName("ProductSuggestionList") as ListBox;
                if (nameInput == null || suggestions == null)
                    throw new InvalidOperationException("QA: product suggestion controls are missing.");
                nameInput.Text = "شاي";
                view.UpdateLayout();
                if (suggestions.Items.Count == 0)
                    throw new InvalidOperationException("QA: product name suggestions did not appear.");
                // Suggestion is optional and must not silently overwrite what was typed.
                if (nameInput.Text != "شاي")
                    throw new InvalidOperationException("QA: product suggestion forced a different name.");

                var noteList = view.FindName("NotesListBox") as ListBox;
                if (noteList == null || noteList.Items.Count == 0)
                    throw new InvalidOperationException("QA: note editor did not load persisted notes.");
                // Keep a blank editor for the image; loading a note is tested separately.

                var root = (FrameworkElement)view.Content;
                var screens = new [] {
                    new {Index=0,Name="01-dashboard.png"},
                    new {Index=1,Name="02-products-stock.png"},
                    new {Index=2,Name="03-suppliers.png"},
                    new {Index=3,Name="04-purchases.png"},
                    new {Index=4,Name="05-sales-barcode.png"},
                    new {Index=5,Name="06-invoices.png"},
                    new {Index=6,Name="07-notes.png"},
                    new {Index=7,Name="08-settings.png"},
                    new {Index=8,Name="09-stock-movements.png"}
                };
                foreach(var screen in screens)
                {
                    tabs.SelectedIndex=screen.Index;
                    view.UpdateLayout();
                    Save(root,Path.Combine(directory,screen.Name));
                }
                var receiptPanel=view.FindName("GoodsReceiptPanel") as SupplyVoucherControl;
                if (receiptPanel==null || tabs.Items.Count!=10)
                    throw new InvalidOperationException("QA: receipt screen missing from navigation.");
                receiptPanel.LoadVoucher(approved.VoucherID);
                tabs.SelectedIndex=9; view.UpdateLayout();
                Save(root,Path.Combine(directory,"12-goods-receipt.png"));
                // Exercise compact displays in addition to the 1390px desktop viewport.
                view.Width = 1024; view.Height = 768;
                tabs.SelectedIndex = 0; view.UpdateLayout();
                Save(root, Path.Combine(directory, "10-compact-dashboard.png"));
                tabs.SelectedIndex = 1; view.UpdateLayout();
                Save(root, Path.Combine(directory, "11-compact-catalog.png"));
                tabs.SelectedIndex=9; view.UpdateLayout();
                Save(root, Path.Combine(directory, "13-compact-goods-receipt.png"));
                // Verify the ORIGINAL invoice, not the later tier-price QA sale.
                var postedSale=service.Documents().Where(d=>d.Type=="SALE").OrderBy(d=>d.Id).First();
                var invoice=service.ReadSaleReceipt(postedSale.Code);
                if(invoice.Lines.Count==0 || !invoice.Lines.Any(x=>x.ProductName=="شاي سادة 250 جم"))
                    throw new InvalidOperationException("QA: catalog edit changed posted sale item names.");
                var xml=new System.Xml.XmlDocument { XmlResolver=null };
                xml.LoadXml(System.Text.Encoding.UTF8.GetString(SaleReceiptSvg.Render(invoice)));
                if(xml.DocumentElement==null || xml.DocumentElement.LocalName!="svg")
                    throw new InvalidOperationException("QA: SVG mobile receipt is invalid.");
                var receiptWindow=new SaleReceiptWindow(invoice,null)
                {
                    Owner=view, Width=680,Height=760
                };
                receiptWindow.Show();
                receiptWindow.UpdateLayout();
                Save((FrameworkElement)receiptWindow.Content,
                     Path.Combine(directory,"14-sale-receipt-qr.png"));
                receiptWindow.Close();
                var employeesWindow = new UserManagementWindow(service)
                {
                    Owner=view, Width=1024, Height=820
                };
                employeesWindow.Show();
                employeesWindow.UpdateLayout();
                Save((FrameworkElement)employeesWindow.Content,
                    Path.Combine(directory,"15-employee-permissions.png"));
                employeesWindow.Close();
                view.Close();
                Application.Current.Shutdown();
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.ToString());
                var dir = Environment.GetEnvironmentVariable("OFFLINESTORE_CAPTURE_DIR");
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                    File.WriteAllText(Path.Combine(dir, "error.txt"), error.ToString());
                }
                throw;
            }
        }
    }
}
#endif
