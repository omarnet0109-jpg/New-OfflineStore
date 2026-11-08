using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Globalization;
using OfflineStore.Infrastructure;
using OfflineStore.Models;

namespace OfflineStore.Services
{
    public sealed class SaleReceiptLine
    {
        public string ProductName { get; set; }
        public string UnitName { get; set; }
        public long EnteredQuantityMilli { get; set; }
        public long UnitPriceCents { get; set; }
        public long LineTotalCents { get; set; }
        public bool LegacyName { get; set; }
        public string PriceLevel { get; set; }
        public string PriceLevelText
        {
            get { return PriceLevel=="WHOLESALE" ? "جملة" :
                PriceLevel=="SEMI" ? "نص قطاعي" :
                PriceLevel=="RETAIL" ? "قطاعي" : ""; }
        }
        public string QuantityText { get { return Amounts.Qty(EnteredQuantityMilli) + " " + UnitName; } }
    }

    public sealed class SaleReceiptData
    {
        public string Code { get; set; }
        public string CreatedUtc { get; set; }
        public string Employee { get; set; }
        public long TotalCents { get; set; }
        public List<SaleReceiptLine> Lines { get; set; } = new List<SaleReceiptLine>();
    }

    /// <summary>Read-only representation of an immutable POSTED sale.</summary>
    public sealed class SaleReceiptService
    {
        private readonly StoreDb _db;
        public SaleReceiptService(StoreDb db) { _db = db; }

        public SaleReceiptData Read(string code)
        {
            if (string.IsNullOrWhiteSpace(code) || code.Length > 32 ||
                !code.StartsWith("S-", StringComparison.Ordinal))
                throw new ArgumentException("رقم فاتورة البيع غير صالح.");
            var result = new SaleReceiptData();
            using (var db = _db.Open())
            {
                using (var cmd = new SQLiteCommand(@"SELECT d.doc_code,d.created_utc,
                    d.total_cents,COALESCE(u.display_name,'')
                    FROM documents d LEFT JOIN users u ON u.id=d.created_by_user_id
                    WHERE d.doc_code=@code AND d.doc_type='SALE' AND d.state='POSTED'", db))
                {
                    cmd.Parameters.AddWithValue("@code", code);
                    using (var reader = cmd.ExecuteReader())
                    {
                        if (!reader.Read()) throw new ArgumentException("فاتورة البيع غير موجودة أو غير معتمدة.");
                        result.Code = reader.GetString(0);
                        result.CreatedUtc = reader.GetString(1);
                        result.TotalCents = reader.GetInt64(2);
                        result.Employee = reader.GetString(3);
                    }
                }
                using (var cmd = new SQLiteCommand(@"SELECT
                    COALESCE(s.name_at_sale,p.name),l.entered_unit_name,
                    l.entered_qty_milli,l.entered_unit_price_cents,l.line_total_cents,
                    COALESCE(s.snapshot_source,'MIGRATED'),l.sale_price_level
                    FROM document_lines l
                    JOIN documents d ON d.id=l.document_id
                    JOIN products p ON p.id=l.product_id
                    LEFT JOIN sale_line_names s ON s.document_line_id=l.id
                    WHERE d.doc_code=@code AND d.doc_type='SALE' AND d.state='POSTED'
                    ORDER BY l.line_no", db))
                {
                    cmd.Parameters.AddWithValue("@code", code);
                    using (var reader = cmd.ExecuteReader())
                        while (reader.Read())
                            result.Lines.Add(new SaleReceiptLine {
                                ProductName=reader.GetString(0), UnitName=reader.GetString(1),
                                EnteredQuantityMilli=reader.GetInt64(2),
                                UnitPriceCents=reader.GetInt64(3), LineTotalCents=reader.GetInt64(4),
                                LegacyName=reader.GetString(5)=="MIGRATED",
                                PriceLevel=reader.IsDBNull(6) ? null : reader.GetString(6)
                            });
                }
            }
            if (result.Lines.Count == 0)
                throw new InvalidOperationException("فاتورة بلا سطور. توقف عن إعادة طباعتها وراجع قاعدة البيانات.");
            long sum=0;
            foreach(var line in result.Lines)
                sum=checked(sum+line.LineTotalCents);
            if (sum!=result.TotalCents)
                throw new InvalidOperationException("إجمالي الفاتورة غير مطابق للسطور؛ لا يمكن إعادة طباعتها.");
            return result;
        }
    }

    public static class SaleReceiptSvg
    {
        private static string Esc(string value)
        {
            return System.Security.SecurityElement.Escape(value ?? "") ?? "";
        }
        private static string Money(long cents)
        {
            return (cents/100m).ToString("0.00",CultureInfo.InvariantCulture)+" EGP";
        }
        private static void Label(System.Text.StringBuilder sb, long y, string text, int size = 23, bool bold = false)
        {
            sb.Append("<text x='550' y='").Append(y).Append("' text-anchor='end' direction='rtl' ")
                .Append("font-family='Tahoma,Arial,sans-serif' font-size='").Append(size)
                .Append("' fill='#18283f'").Append(bold ? " font-weight='bold'" : "")
                .Append(">").Append(Esc(text)).Append("</text>");
        }
        /// <summary>Complete offline SVG receipt image; no remote assets or scripts.</summary>
        public static byte[] Render(SaleReceiptData receipt)
        {
            if (receipt == null) throw new ArgumentNullException("receipt");
            int height = checked(250 + receipt.Lines.Count * 84 + 200);
            var sb = new System.Text.StringBuilder(1500 + receipt.Lines.Count * 280);
            sb.Append("<svg xmlns='http://www.w3.org/2000/svg' width='600' height='")
              .Append(height).Append("' viewBox='0 0 600 ").Append(height).Append("'>")
              .Append("<rect width='600' height='100%' fill='#ffffff'/>")
              .Append("<rect x='20' y='16' width='560' height='92' rx='14' fill='#e7f1fc'/>");
            Label(sb, 55, "OfflineStore — فاتورة بيع", 32, true);
            Label(sb, 91, "رقم الفاتورة: "+receipt.Code, 20);
            Label(sb, 149, "تاريخ UTC: "+receipt.CreatedUtc, 18);
            Label(sb, 186, "الموظف: "+receipt.Employee, 18);
            long y=235;
            foreach (var line in receipt.Lines)
            {
                sb.Append("<line x1='35' y1='").Append(y-24).Append("' x2='565' y2='")
                  .Append(y-24).Append("' stroke='#e0e7ef'/>");
                Label(sb,y,line.ProductName,21,true);
                Label(sb,y+36,line.QuantityText+" × "+Money(line.UnitPriceCents) +
                    (line.PriceLevelText.Length == 0 ? "" : " — "+line.PriceLevelText),18);
                sb.Append("<text x='48' y='").Append(y+36)
                  .Append("' font-family='Arial,sans-serif' font-size='18' fill='#205b91'>")
                  .Append(Esc(Money(line.LineTotalCents))).Append("</text>");
                y+=84;
            }
            sb.Append("<rect x='20' y='").Append(y).Append("' width='560' height='75' rx='12' fill='#e7f1fc'/>");
            Label(sb,y+47,"الإجمالي: "+Money(receipt.TotalCents),27,true);
            y+=108;
            Label(sb,y,"نسخة من فاتورة بيع معتمدة ومحفوظة محليًا",17);
            if (receipt.Lines.Exists(x=>x.LegacyName))
                Label(sb,y+32,"تنبيه: بعض أسماء الأصناف القديمة نُقلت عند الترقية.",14);
            sb.Append("</svg>");
            return System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        }
    }
}
