using System;
using System.Data.SQLite;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using OfflineStore.Infrastructure;

namespace OfflineStore.Services
{
    /// <summary>
    /// Serves only individually signed, posted-sale SVG receipts on one private
    /// LAN interface. This is NOT an internet/cloud service, nor a web portal.
    /// Must be stopped when the desktop application exits.
    /// </summary>
    public sealed class OfflineReceiptServer : IDisposable
    {
        public const int Port = 18765;
        private readonly StoreService _store;
        private readonly byte[] _key;
        private readonly TcpListener _listener;
        private volatile bool _closed;
        private readonly IPAddress _ip;
        private readonly object _gate = new object();
        private int _active;

        private OfflineReceiptServer(StoreService store, IPAddress ip, byte[] key)
        {
            _store = store;
            _ip = ip;
            _key = key;
            _listener = new TcpListener(ip, Port);
            _listener.Start(8);
            Task.Run((Action)Listen);
        }

        public string BaseAddress { get { return "http://" + _ip + ":" + Port; } }

        public string LinkForSale(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || !code.StartsWith("S-", StringComparison.Ordinal))
                throw new ArgumentException("هذا الرابط مخصص لفواتير البيع المعتمدة.");
            // Verify the invoice before exposing a link.
            _store.ReadSaleReceipt(code);
            return BaseAddress + "/receipt/" + code + "/" + Signature(code);
        }

        private string Signature(string code)
        {
            using(var mac=new HMACSHA256(_key))
                return BitConverter.ToString(mac.ComputeHash(Encoding.UTF8.GetBytes(code)))
                    .Replace("-", "").ToLowerInvariant();
        }

        private static bool Equal(string x, string y)
        {
            if (x == null || y == null || x.Length != y.Length) return false;
            int diff=0;
            for(int i=0;i<x.Length;i++) diff |= (x[i] ^ y[i]);
            return diff==0;
        }

        private static bool IsPrivate(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            var a=ip.GetAddressBytes();
            return a[0]==10 || (a[0]==172 && a[1]>=16 && a[1]<=31) ||
                (a[0]==192 && a[1]==168);
        }

        private static IPAddress FindLocalLanAddress()
        {
            foreach(var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if(adapter.OperationalStatus != OperationalStatus.Up) continue;
                if(adapter.NetworkInterfaceType != NetworkInterfaceType.Wireless80211 &&
                   adapter.NetworkInterfaceType != NetworkInterfaceType.Ethernet) continue;
                foreach(var address in adapter.GetIPProperties().UnicastAddresses)
                    if(IsPrivate(address.Address)) return address.Address;
            }
            return null;
        }

        private static byte[] ReadKey(StoreDb store)
        {
            using(var c=store.Open())
            using(var tx=c.BeginTransaction())
            {
                byte[] key;
                using(var find=new SQLiteCommand("SELECT secret FROM receipt_secret WHERE id=1", c, tx))
                    key=find.ExecuteScalar() as byte[];
                if(key==null)
                {
                    var created=new byte[32];
                    using(var rng=RandomNumberGenerator.Create()) rng.GetBytes(created);
                    using(var insert=new SQLiteCommand(
                        "INSERT OR IGNORE INTO receipt_secret(id,secret) VALUES(1,@secret)",c,tx))
                    {
                        insert.Parameters.AddWithValue("@secret",created);
                        insert.ExecuteNonQuery();
                    }
                    using(var find=new SQLiteCommand("SELECT secret FROM receipt_secret WHERE id=1",c,tx))
                        key=find.ExecuteScalar() as byte[];
                }
                tx.Commit();
                if(key==null || key.Length!=32)
                    throw new InvalidOperationException("مفتاح مشاركة الفواتير غير صالح.");
                return key;
            }
        }

        public static OfflineReceiptServer TryStart(StoreService store)
        {
            if(store==null || (!store.Can("SALE") && !store.Can("REPORTS"))) return null;
            var local=FindLocalLanAddress();
            if(local==null) return null;
            try { return new OfflineReceiptServer(store,local,ReadKey(new StoreDb())); }
            catch(SocketException) { return null; }
        }

        private void Listen()
        {
            while(!_closed)
            {
                TcpClient client;
                try { client=_listener.AcceptTcpClient(); }
                catch(SocketException) { break; }
                catch(ObjectDisposedException) { break; }
                if(System.Threading.Interlocked.Increment(ref _active)>4)
                {
                    System.Threading.Interlocked.Decrement(ref _active);
                    client.Close();
                    continue;
                }
                Task.Run(() =>
                {
                    try { Respond(client); }
                    catch(Exception) { /* Fail closed: no local details to callers. */ }
                    finally
                    {
                        client.Close();
                        System.Threading.Interlocked.Decrement(ref _active);
                    }
                });
            }
        }

        private static void WriteResponse(Stream stream,int status,string description,byte[] bytes)
        {
            if(bytes==null) bytes=new byte[0];
            var header=Encoding.ASCII.GetBytes(
                "HTTP/1.1 "+status+" "+description+"\r\n"+
                "Content-Type: image/svg+xml; charset=utf-8\r\n"+
                "Cache-Control: no-store\r\n"+
                "X-Content-Type-Options: nosniff\r\n"+
                "Content-Security-Policy: default-src 'none'; sandbox\r\n"+
                "Content-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n");
            stream.Write(header,0,header.Length);
            stream.Write(bytes,0,bytes.Length);
        }

        private void Respond(TcpClient client)
        {
            client.ReceiveTimeout=4000;
            client.SendTimeout=4000;
            using(var stream=client.GetStream())
            {
                var request=new MemoryStream();
                int b;
                // Strict limit avoids an unbounded request-line allocation.
                while(request.Length<1024 && (b=stream.ReadByte())>=0)
                {
                    if(b==10)break;
                    request.WriteByte((byte)b);
                }
                if(request.Length>=1024){WriteResponse(stream,400,"Bad Request",null);return;}
                var line=Encoding.ASCII.GetString(request.ToArray()).Trim();
                var parts=line.Split(' ');
                if(parts.Length!=3 || parts[0]!="GET" || !parts[2].StartsWith("HTTP/1."))
                { WriteResponse(stream,400,"Bad Request",null);return; }
                var route=parts[1].Split('/');
                if(route.Length!=4 || route[0]!="" || route[1]!="receipt" ||
                   route[2].Length>32 || route[3].Length!=64 ||
                   !route[2].StartsWith("S-",StringComparison.Ordinal))
                { WriteResponse(stream,404,"Not Found",null);return; }
                var code=route[2];
                if(!Equal(route[3],Signature(code)))
                { WriteResponse(stream,404,"Not Found",null);return; }
                try
                {
                    var invoice=_store.ReadSaleReceipt(code);
                    WriteResponse(stream,200,"OK",SaleReceiptSvg.Render(invoice));
                }
                catch(Exception)
                {
                    WriteResponse(stream,404,"Not Found",null);
                }
            }
        }

        public void Dispose()
        {
            lock(_gate)
            {
                if(_closed) return;
                _closed=true;
                _listener.Stop();
            }
        }
    }
}
