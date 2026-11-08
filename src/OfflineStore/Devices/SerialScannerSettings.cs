using System;
using System.Data.SQLite;
using System.Globalization;
using System.IO.Ports;
using OfflineStore.Infrastructure;

namespace OfflineStore.Devices
{
    public sealed class SerialScannerSettings
    {
        public bool Enabled { get; set; }
        public string PortName { get; set; } = "";
        public int BaudRate { get; set; } = 9600;
        public int DataBits { get; set; } = 8;
        public Parity Parity { get; set; } = System.IO.Ports.Parity.None;
        public StopBits StopBits { get; set; } = System.IO.Ports.StopBits.One;
        public Handshake Handshake { get; set; } = System.IO.Ports.Handshake.None;

        public void Validate(bool requirePort)
        {
            if ((requirePort || Enabled) && string.IsNullOrWhiteSpace(PortName))
                throw new ArgumentException("اختر منفذ COM أولًا.");
            // Fail-closed port name validation; Windows accepts COM1..COM256, with COM10+ via driver.
            if (!string.IsNullOrWhiteSpace(PortName))
            {
                var port = PortName.Trim().ToUpperInvariant();
                if (!port.StartsWith("COM", StringComparison.Ordinal) ||
                    !int.TryParse(port.Substring(3), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ||
                    number < 1 || number > 256)
                    throw new ArgumentException("اسم منفذ COM غير صالح.");
                PortName = "COM" + number.ToString(CultureInfo.InvariantCulture);
            }
            if (BaudRate != 1200 && BaudRate != 2400 && BaudRate != 4800 &&
                BaudRate != 9600 && BaudRate != 19200 && BaudRate != 38400 && BaudRate != 57600 && BaudRate != 115200)
                throw new ArgumentException("سرعة الاتصال غير مدعومة.");
            if (DataBits != 7 && DataBits != 8) throw new ArgumentException("Data Bits يجب أن تكون 7 أو 8.");
            if (!Enum.IsDefined(typeof(Parity), Parity) || !Enum.IsDefined(typeof(StopBits), StopBits) ||
                !Enum.IsDefined(typeof(Handshake), Handshake) || StopBits == System.IO.Ports.StopBits.None)
                throw new ArgumentException("إعدادات الاتصال غير صالحة.");
        }
    }

    public sealed class SerialScannerSettingsStore
    {
        private readonly StoreDb _db;
        public SerialScannerSettingsStore(StoreDb db) { _db = db; }

        public SerialScannerSettings Load()
        {
            using (var c = _db.Open())
            using (var cmd = new SQLiteCommand("SELECT enabled,port_name,baud_rate,data_bits,parity,stop_bits,handshake FROM serial_scanner_config WHERE id=1", c))
            using (var reader = cmd.ExecuteReader())
            {
                if (!reader.Read()) return new SerialScannerSettings();
                var data = new SerialScannerSettings
                {
                    Enabled = reader.GetInt64(0) == 1,
                    PortName = reader.GetString(1),
                    BaudRate = reader.GetInt32(2),
                    DataBits = reader.GetInt32(3),
                    Parity = (Parity)Enum.Parse(typeof(Parity), reader.GetString(4), false),
                    StopBits = (StopBits)Enum.Parse(typeof(StopBits), reader.GetString(5), false),
                    Handshake = (Handshake)Enum.Parse(typeof(Handshake), reader.GetString(6), false)
                };
                data.Validate(false);
                return data;
            }
        }

        public void Save(SerialScannerSettings data, long actorUserId)
        {
            data.Validate(false);
            using (var c = _db.Open())
            using (var tx = c.BeginTransaction())
            {
                using (var cmd = new SQLiteCommand(@"UPDATE serial_scanner_config
                    SET enabled=@enabled,port_name=@port,baud_rate=@baud,data_bits=@bits,
                    parity=@parity,stop_bits=@stop,handshake=@handshake,updated_utc=@updated WHERE id=1", c, tx))
                {
                    cmd.Parameters.AddWithValue("@enabled", data.Enabled ? 1 : 0);
                    cmd.Parameters.AddWithValue("@port", data.PortName);
                    cmd.Parameters.AddWithValue("@baud", data.BaudRate);
                    cmd.Parameters.AddWithValue("@bits", data.DataBits);
                    cmd.Parameters.AddWithValue("@parity", data.Parity.ToString());
                    cmd.Parameters.AddWithValue("@stop", data.StopBits.ToString());
                    cmd.Parameters.AddWithValue("@handshake", data.Handshake.ToString());
                    cmd.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
                using (var cmd = new SQLiteCommand(@"INSERT INTO audit_log
                    (action,entity_id,actor_user_id,details,created_utc)
                    VALUES('SERIAL_SCANNER_CONFIG',1,@user,'',@utc)", c, tx))
                {
                    cmd.Parameters.AddWithValue("@user", actorUserId);
                    cmd.Parameters.AddWithValue("@utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
    }
}
