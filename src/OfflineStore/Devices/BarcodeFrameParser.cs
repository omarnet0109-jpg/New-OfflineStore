using System;
using System.Collections.Generic;
using System.Text;

namespace OfflineStore.Devices
{
    // Serial callbacks can split a barcode at ANY byte. A scan ends only on CR/LF/ETX.
    // Barcode suffix CR or CRLF must be enabled in the scanner's own configuration.
    public sealed class BarcodeFrameParser
    {
        private const int MaxLength = 128;
        private readonly StringBuilder _pending = new StringBuilder();
        private bool _discarding;

        public IList<string> Feed(string text)
        {
            var completed = new List<string>();
            if (text == null) return completed;
            foreach (char ch in text)
            {
                if (ch == '\u0002') // STX resets framing
                {
                    _pending.Clear();
                    _discarding = false;
                }
                else if (ch == '\r' || ch == '\n' || ch == '\u0003')
                {
                    if (!_discarding && _pending.Length > 0)
                    {
                        var code = _pending.ToString().Trim();
                        if (code.Length > 0) completed.Add(code);
                    }
                    _pending.Clear();
                    _discarding = false;
                }
                else if (ch >= 32 && ch <= 126)
                {
                    if (!_discarding)
                    {
                        if (_pending.Length >= MaxLength)
                        {
                            _pending.Clear();
                            _discarding = true; // Never accept a truncated code.
                        }
                        else _pending.Append(ch);
                    }
                }
                else
                {
                    // Never join fragments around a corrupt byte into another valid code.
                    _pending.Clear();
                    _discarding = true;
                }
            }
            return completed;
        }

        public void Reset()
        {
            _pending.Clear();
            _discarding = false;
        }
    }
}
