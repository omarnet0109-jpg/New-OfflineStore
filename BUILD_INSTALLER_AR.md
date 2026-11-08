# دليل بناء مثبت OfflineStore النهائي (Setup.exe) — إصدار الإنتاج v1.0

هذا الدليل يوضح خطوات بناء مثبت الإنتاج النهائي `OfflineStore_Setup_v1.0.exe` المتكامل لحزمة OfflineStore لنظام التشغيل Windows 10 x86 (.NET Framework 4.8 وWPF وSQLite).

## 1. مواصفات مثبت الإنتاج النهائي
- **اسم المثبت الشامل:** `OfflineStore_Setup_v1.0.exe`
- **نوع المثبت:** Nullsoft Scriptable Install System (NSIS v3.x)
- **البيئة المضمنة:** حزمة التثبيت الأوفلاين الرسمية لـ Microsoft .NET Framework 4.8 (`NDP48-x86-x64-AllOS-ENU.exe`) بحيث يعمل المثبت على أي جهاز بدون اتصال بالإنترنت.
- **مكتبات SQLite الأصلية:** SQLite 3.54.1 الأصلية المخصصة لمنصة x86 (`sqlite3.dll` و`e_sqlite3.dll`).
- **أمان البيانات:** المثبت والمزيل (`Uninstall.exe`) لا يقومان بحذف أو تعديل قاعدة بيانات البرنامج الواقعة في `%LOCALAPPDATA%\OfflineStore\store.db`.

## 2. البناء التلقائي عبر GitHub Actions (Windows Runner)
المشروع مجهز بالكامل بسير عمل GitHub Actions في المسار:
`.github/workflows/windows-build.yml`

### خطوات البناء المؤتمت:
1. رفع كود المشروع إلى مستودع GitHub (`omarnet0109-jpg/OfflineStore`).
2. تفعيل سير العمل التلقائي `OfflineStore Windows Build & Setup v1.0 (Production Release)` على خادم `windows-2022`.
3. يقوم الخادم بالآتي:
   - فحص واجتياز جميع اختبارات SQLite وPython والتحقق من التشفير.
   - تنزيل والتحقق من صحة مكتبات SQLite x86 الرسمية.
   - تجميع وتشغيل اختبارات C# للبنية التحتية (`InfrastructureTests.exe` بما فيها اختبارات العكس والحسابات والمرتجعات).
   - بناء تطبيق WPF بصيغة Release x86 عبر MSBuild.
   - بناء المثبت الشامل عبر NSIS ودمج حزمة .NET Framework 4.8.
   - إجراء اختبارات التثبيت الصامت (`/S`) وإلغاء التثبيت والتحقق من بقاء بيانات العميل.
   - نشر الإصدار النهائي تلقائيًا كـ GitHub Release مع رفع `OfflineStore_Setup_v1.0.exe` ورموز التجزئة SHA-256.

## 3. البناء اليدوي على بيئة Windows محلية
عند الرغبة في البناء محليًا على جهاز Windows:
1. المتطلبات: Visual Studio 2022 (مع مكونات .NET Framework 4.8) + NSIS 3.x.
2. تجميع المشروع:
   ```cmd
   msbuild src/OfflineStore/OfflineStore.csproj /p:Configuration=Release /p:Platform=AnyCPU /p:PlatformTarget=x86 /restore
   ```
3. تنزيل حزمة الدوت نت أوفلاين ووضعها في `dist/prerequisites/NDP48-x86-x64-AllOS-ENU.exe`.
4. تجميع المثبت عبر NSIS:
   ```cmd
   makensis /DINSTALLER_QA /DAPP_PAYLOAD=src\OfflineStore\bin\Release /DDOTNET_REDIST=dist\prerequisites\NDP48-x86-x64-AllOS-ENU.exe /DOUTPUT_EXE=dist\OfflineStore_Setup_v1.0.exe installer\OfflineStore.AllInOne.nsi
   ```
