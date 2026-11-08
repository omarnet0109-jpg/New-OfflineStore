using System;
using System.Linq;
using OfflineStore.Devices;

public static class Program
{
    private static int _cases;
    private static void Verify(bool value, string message)
    {
        _cases++;
        if (!value) throw new Exception("Barcode parser failed: " + message);
    }
    public static int Main()
    {
        var p = new BarcodeFrameParser();
        Verify(p.Feed("1234").Count == 0, "partial frame");
        Verify(p.Feed("567\r").SequenceEqual(new[] {"1234567"}), "split frame");
        Verify(p.Feed("\n").Count == 0, "CRLF must not create a second scan");
        Verify(p.Feed("A1\rB2\n").SequenceEqual(new[] {"A1", "B2"}), "several scans in one chunk");
        Verify(p.Feed("\u0002CODE\u0003").SequenceEqual(new[] {"CODE"}), "STX / ETX framing");
        Verify(p.Feed(new string('X', 129) + "\r").Count == 0, "oversized frame must be discarded");
        Verify(p.Feed("NEXT\r").SequenceEqual(new[] {"NEXT"}), "recovery after oversized frame");
        Verify(p.Feed("BAD").Count == 0, "unfinished frame");
        p.Reset();
        Verify(p.Feed("OK\r").SequenceEqual(new[] {"OK"}), "reset clears partial data");
        Verify(p.Feed("\r\n").Count == 0, "empty framing");
        Verify(p.Feed("AB\u0000CD\r").Count == 0, "NUL must discard the whole frame");
        Verify(p.Feed("AB\tCD\r").Count == 0, "tab must not join two code fragments");
        Verify(p.Feed("AB\u007fCD\r").Count == 0, "DEL must discard the whole frame");
        Verify(p.Feed("ABéCD\r").Count == 0, "non-ASCII data must not become a different barcode");
        Verify(p.Feed("BAD\u0000").Count == 0, "invalid split frame");
        Verify(p.Feed("TAIL\rNEXT\r").SequenceEqual(new[] { "NEXT" }), "discard through terminator then recover");
        Verify(p.Feed("BAD\t\u0002OK\u0003").SequenceEqual(new[] { "OK" }), "STX starts a new clean frame");
        Console.WriteLine("PASS: " + _cases + " serial parser cases");
        return 0;
    }
}
