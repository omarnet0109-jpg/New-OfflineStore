using System;
using System.IO.Ports;
using System.Text;

namespace OfflineStore.Devices
{
    // Owns the serial port. Callbacks are raised on a background thread.
    public sealed class SerialBarcodeScanner : IDisposable
    {
        private readonly SerialPort _port;
        private readonly BarcodeFrameParser _parser = new BarcodeFrameParser();
        private readonly object _parserLock = new object();
        private bool _disposed;

        public event Action<string> BarcodeScanned;
        public event Action<string> DeviceError;
        public bool IsOpen { get { return !_disposed && _port.IsOpen; } }

        public SerialBarcodeScanner(SerialScannerSettings settings)
        {
            settings.Validate(true);
            _port = new SerialPort(settings.PortName, settings.BaudRate, settings.Parity,
                settings.DataBits, settings.StopBits)
            {
                Handshake = settings.Handshake,
                Encoding = Encoding.ASCII,
                ReadTimeout = 500,
                ReceivedBytesThreshold = 1
            };
            _port.DataReceived += OnDataReceived;
            _port.ErrorReceived += OnErrorReceived;
        }

        public void Start()
        {
            if (_disposed) throw new ObjectDisposedException("SerialBarcodeScanner");
            if (!_port.IsOpen) _port.Open();
        }

        private void OnDataReceived(object sender, SerialDataReceivedEventArgs args)
        {
            try
            {
                // A DataReceived event does NOT necessarily contain a full barcode.
                var chunk = _port.ReadExisting();
                lock (_parserLock)
                {
                    foreach (var code in _parser.Feed(chunk))
                    {
                        var handler = BarcodeScanned;
                        if (handler != null) handler(code);
                    }
                }
            }
            catch (ObjectDisposedException) { /* Port closed during shutdown. */ }
            catch (InvalidOperationException) { /* Port closed during shutdown. */ }
            catch (Exception ex) { RaiseError("خطأ أثناء قراءة قارئ الباركود: " + ex.Message); }
        }
        private void OnErrorReceived(object sender, SerialErrorReceivedEventArgs args)
        {
            RaiseError("خطأ في اتصال قارئ الباركود: " + args.EventType);
        }
        private void RaiseError(string message)
        {
            var handler = DeviceError;
            if (handler != null) handler(message);
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _port.DataReceived -= OnDataReceived;
            _port.ErrorReceived -= OnErrorReceived;
            try { if (_port.IsOpen) _port.Close(); }
            finally { _port.Dispose(); }
        }
    }
}
