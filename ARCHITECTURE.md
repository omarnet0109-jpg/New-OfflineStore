# Architecture v0.3 — قرارات تنفيذية

- برنامج جهاز واحد، WPF + .NET Framework 4.8 + C# 7.3 + SQLite 3.x x86؛ تشغيل محلي بلا خدمات سحابية.
- `StoreDb` يدير قاعدة SQLite و3 مستويات من المخطط: v1 (الأصناف/الوثائق/حركات المخزون)، v2 (المظهر وكلمة مرور قديمة)، v3 (الوحدات والمستخدمون والتنبيهات والضوابط المحاسبية).
- جدول `product_units` لكل صنف: الاسم وعامل التحويل `factor_milli` وسعر البيع الخاص بتلك الوحدة وباركود اختياري. الوحدة الأساسية دائمًا عاملها 1000 (=وحدة واحدة). الدستة 12000؛ والكرتونة عاملها يحدده المستخدم لكل صنف، لا توجد أحجام ثابتة عامة.
- `document_lines.qty_milli` يظل **رصيد الوحدة الأساسية بالألف**، بينما الأعمدة الجديدة `entered_qty_milli` و`entered_unit_name` و`unit_factor_milli` و`entered_unit_price_cents` تحفظ طريقة الإدخال الأصلية. حساب `line_total_cents` من الكمية المُدخلة × سعر الوحدة المختارة، بتقريب نصف لأعلى إلى أصغر فئة نقدية.
- يحافظ دفتر حركات المخزون على عدم السالب، ويمنع تعديل وثائق POSTED. Trigger إضافي يتحقق قبل اعتماد الفاتورة من تساوي إجمالي الأسطر وإجمالي الوثيقة ومن عدم نقص الحركات.
- لحسابات الموظفين `users` كلمات مرور مشتقة PBKDF2-HMAC-SHA256 وsalt فريد، محاولات محدودة، أدوار `ADMIN/MANAGER/CASHIER/STOCK`؛ يتحقق `StoreService` من الصلاحيات قبل الكتابة ويتحقق من أسعار الكاشير عند ترحيل البيع.
- لا تدعم نسخة v0.3 تغيير عامل الوحدة بعد إنشائها، منعًا لفساد مستندات قديمة. لتغيير تعبئة منتج قائم يلزم تدفق إيقاف/إنشاء وحدة أخرى وإجراء تسوية واضح في إصدار لاحق.
- `v_low_stock` يصدر قائمة الأصناف النشطة التي رصيدها ≤ حد إعادة الطلب (مع تجاوز الأسماء ذات الحد 0).
- الأموال أعداد صحيحة بأصغر وحدة نقدية؛ الكميات بالميلي (`long`)؛ العمليات المترابطة بداخل SQLite Transaction؛ الفواتير تاريخية ولا تتغير بتعديل سعر الوحدة.
- آخر تكلفة محفوظة لكل وحدة أساسية هي قيمة تقريبية آخر سعر شراء. لا يوجد بعد نظام تكلفة مخزون وتقييم أرباح محاسبي صحيح؛ لا يجوز تسويق نسخة v0.3 بأنها محاسبة معتمدة.
- البيانات تحت `%LOCALAPPDATA%` لحساب Windows واحد؛ حسابات الموظفين داخل التطبيق. الأدوار لا تحمي ملف SQLite من شخص يملك صلاحية الوصول له على Windows.
- اختبار Python SQLite يثبت القيود والهجرة فقط، وليس C#/.NET أو Windows. CI Windows build/Artifact لا يكفي كاختبار قبول على Win7 SP1.


## v0.4 — COM / RS-232 Barcode Adapter

- `Devices/BarcodeFrameParser.cs`: stateful framing from event chunks, CR/LF/ETX suffix; STX resets; 128 ASCII printable characters max; long/truncated codes are dropped. It does **not** guess missing delimiters.
- `Devices/SerialBarcodeScanner.cs`: owns `SerialPort`, observes event on a worker thread. The View dispatches output onto WPF's UI dispatcher. Disposal releases the COM port.
- `Devices/SerialScannerSettings.cs`: validates persisted settings. Configuration is persisted under SQLite migration 004 and only ADMIN (`USERS` permission) can change it.
- `SerialScannerSettingsWindow.xaml`: device config and test scan. The production port is closed before the testing dialog opens.
- `MainViewModel.ApplyScannedBarcode`: display-only preview, no automatic sale, transaction, or inventory mutation. Existing `StoreService.FindUnitByCode` resolves code/unit.
- Serial loss/reconnect not auto-recovered; COM hardware-specific compatibility remains pending. Barcode parser needs a scanner suffix CR/LF/CRLF or ETX. COM default is disabled.
- See Windows CI `tests/serial_parser`: standalone .NET Framework 4.8 executable tests for frame fragmentation, CRLF, STX/ETX and oversized input. Must run on Windows; **not locally verified on this Linux runtime**.
