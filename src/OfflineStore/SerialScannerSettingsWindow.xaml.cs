using System;
using System.Globalization;
using System.IO.Ports;
using System.Linq;
using System.Windows;
using OfflineStore.Devices;
using OfflineStore.Services;

namespace OfflineStore
{
    public partial class SerialScannerSettingsWindow : Window
    {
        private readonly StoreService _service;
        private SerialBarcodeScanner _test;

        public SerialScannerSettingsWindow(StoreService service)
        {
            _service = service;
            InitializeComponent();
            if (!_service.Can("USERS")) throw new UnauthorizedAccessException("إعدادات الأجهزة للمدير فقط.");
            var saved = _service.LoadScannerSettings();
            BaudCombo.ItemsSource = new[] {1200,2400,4800,9600,19200,38400,57600,115200};
            BitsCombo.ItemsSource = new[] {7,8};
            ParityCombo.ItemsSource = Enum.GetValues(typeof(Parity));
            StopCombo.ItemsSource = new[] {StopBits.One,StopBits.OnePointFive,StopBits.Two};
            HandshakeCombo.ItemsSource = Enum.GetValues(typeof(Handshake));
            RefreshPorts(saved.PortName);
            EnabledBox.IsChecked = saved.Enabled;
            BaudCombo.SelectedItem = saved.BaudRate;
            BitsCombo.SelectedItem = saved.DataBits;
            ParityCombo.SelectedItem = saved.Parity;
            StopCombo.SelectedItem = saved.StopBits;
            HandshakeCombo.SelectedItem = saved.Handshake;
            Closed += (s,e) => StopTest();
        }
        private void RefreshPorts(string keep)
        {
            var ports = SerialPort.GetPortNames().OrderBy(s => s).ToList();
            if (!string.IsNullOrWhiteSpace(keep) && !ports.Contains(keep, StringComparer.OrdinalIgnoreCase))
                ports.Add(keep);
            PortCombo.ItemsSource = ports;
            PortCombo.Text = keep ?? "";
        }
        private void RefreshClick(object sender, RoutedEventArgs e) { RefreshPorts(PortCombo.Text); }

        private SerialScannerSettings CurrentValues()
        {
            if (BaudCombo.SelectedItem == null || BitsCombo.SelectedItem == null ||
                ParityCombo.SelectedItem == null || StopCombo.SelectedItem == null ||
                HandshakeCombo.SelectedItem == null)
                throw new ArgumentException("أكمل بيانات الاتصال.");
            var result = new SerialScannerSettings
            {
                Enabled = EnabledBox.IsChecked == true,
                PortName = (PortCombo.Text ?? "").Trim(),
                BaudRate = (int)BaudCombo.SelectedItem,
                DataBits = (int)BitsCombo.SelectedItem,
                Parity = (Parity)ParityCombo.SelectedItem,
                StopBits = (StopBits)StopCombo.SelectedItem,
                Handshake = (Handshake)HandshakeCombo.SelectedItem
            };
            result.Validate(false);
            return result;
        }
        private void SaveClick(object sender, RoutedEventArgs e)
        {
            try
            {
                _service.SaveScannerSettings(CurrentValues());
                StatusText.Text = "تم الحفظ. سيُعاد فتح الاتصال بالإعدادات الجديدة.";
                DialogResult = true;
            }
            catch (Exception ex) { StatusText.Text = "تعذر الحفظ: " + ex.Message; }
        }
        private void TestClick(object sender, RoutedEventArgs e)
        {
            StopTest();
            try
            {
                var candidate = CurrentValues();
                candidate.Validate(true);
                _test = new SerialBarcodeScanner(candidate);
                _test.BarcodeScanned += OnTestBarcode;
                _test.DeviceError += OnTestError;
                _test.Start();
                StatusText.Text = "متصل بـ " + candidate.PortName + "، امسح باركودًا الآن (مع suffix CR/LF).";
            }
            catch (Exception ex)
            {
                StopTest();
                StatusText.Text = "فشل اتصال الاختبار: " + ex.Message;
            }
        }
        private void OnTestBarcode(string code)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = "تم استلام الباركود: " + code));
        }
        private void OnTestError(string error)
        {
            Dispatcher.BeginInvoke(new Action(() => StatusText.Text = error));
        }
        private void StopTest()
        {
            var previous = _test;
            _test = null;
            if (previous == null) return;
            previous.BarcodeScanned -= OnTestBarcode;
            previous.DeviceError -= OnTestError;
            try { previous.Dispose(); }
            catch (Exception ex) { StatusText.Text = "تعذر إغلاق اختبار COM: " + ex.Message; }
        }
        private void StopTestClick(object sender, RoutedEventArgs e)
        {
            StopTest();
            StatusText.Text = "تم إيقاف الاختبار.";
        }
        private void CloseClick(object sender, RoutedEventArgs e) { Close(); }
    }
}
