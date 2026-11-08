using System;
using System.Globalization;

namespace OfflineStore.Models
{
    public static class Amounts
    {
        public static long ToCents(string input, string name)
        {
            decimal parsed = ParsePositive(input, name, false);
            var scaled = parsed * 100m;
            if (scaled != decimal.Truncate(scaled))
                throw new ArgumentException(name + ": الحد الأقصى منزلتان عشريتان.");
            return checked((long)scaled);
        }

        public static long ToMilli(string input, string name, bool allowZero = false)
        {
            decimal parsed = ParsePositive(input, name, !allowZero);
            var scaled = parsed * 1000m;
            if (scaled != decimal.Truncate(scaled))
                throw new ArgumentException(name + ": الحد الأقصى ثلاث منازل عشرية.");
            return checked((long)scaled);
        }

        private static decimal ParsePositive(string input, string name, bool strict)
        {
            decimal number;
            // Accept the Arabic-Indic digits often typed by staff on Arabic keyboards.
            input = NormalizeArabicDigits(input);
            var valid = decimal.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign |
                NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.CurrentCulture, out number)
                      || decimal.TryParse(input, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign |
                NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite,
                CultureInfo.InvariantCulture, out number);
            if (!valid || number < 0 || (strict && number == 0))
                throw new ArgumentException(name + ": أدخل رقمًا صحيحًا أكبر " + (strict ? "من صفر." : "أو يساوي صفرًا."));
            return number;
        }

        public static string NormalizeArabicDigits(string value)
        {
            if (value == null) return null;
            var output = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (c >= '٠' && c <= '٩') output.Append((char)('0' + c - '٠'));
                else if (c >= '۰' && c <= '۹') output.Append((char)('0' + c - '۰'));
                else if (c == '٫') output.Append('.');
                else output.Append(c);
            }
            return output.ToString();
        }

        public static long BaseQuantity(long enteredQtyMilli, long unitFactorMilli)
        {
            if (enteredQtyMilli <= 0 || unitFactorMilli < 1000 || unitFactorMilli % 1000 != 0)
                throw new ArgumentException("كمية أو تحويل غير صالح.");
            return checked(enteredQtyMilli * (unitFactorMilli / 1000));
        }
        public static long LineTotal(long quantityMilli, long unitPriceCents)
        {
            return checked((long)decimal.Round((decimal)quantityMilli * unitPriceCents / 1000m,
                0, MidpointRounding.AwayFromZero));
        }

        // Base quantity is milli-units; average is milli-cents per base unit.
        // Round only the extended line cost, never the per-unit basis.
        public static long CostTotal(long quantityMilli, long averageCostMillicents)
        {
            if (quantityMilli <= 0 || averageCostMillicents < 0)
                throw new ArgumentException("كمية أو أساس تكلفة غير صالح.");
            return checked((long)decimal.Round((decimal)quantityMilli * averageCostMillicents / 1000000m,
                0, MidpointRounding.AwayFromZero));
        }

        // Difference of cumulative rounded amounts makes full partial returns conserve the original snapshot.
        public static long ProportionalReturn(long originalCents,long returnedBefore,long quantity,long originalQuantity)
        {
            if(originalCents<0 || returnedBefore<0 || quantity<=0 || originalQuantity<=0 || returnedBefore>originalQuantity || quantity>originalQuantity-returnedBefore)
                throw new ArgumentException("كمية المرتجع تتجاوز الفاتورة الأصلية.");
            return checked((long)(decimal.Round((decimal)originalCents*(returnedBefore+quantity)/originalQuantity,0,MidpointRounding.AwayFromZero)
                -decimal.Round((decimal)originalCents*returnedBefore/originalQuantity,0,MidpointRounding.AwayFromZero)));
        }
        public static string Price(long cents)
        {
            return (cents / 100m).ToString("N2", CultureInfo.CurrentCulture);
        }

        public static string EditablePrice(long cents)
        {
            // Form input: never use grouping separators; they can be misread as decimal separators.
            return (cents / 100m).ToString("0.00", CultureInfo.CurrentCulture);
        }

        public static string Qty(long milli)
        {
            return (milli / 1000m).ToString("0.###", CultureInfo.CurrentCulture);
        }
    }

    public sealed class ReceiptPageLayout
    {
        public double Width, Height, Left, Top, Right, Bottom, ContentWidth, FontSize, QrSide;
        public static ReceiptPageLayout ForPrinter(double width, double height,
            double originX, double originY, double printableWidth, double printableHeight)
        {
            foreach (var n in new [] {width,height,originX,originY,printableWidth,printableHeight})
                if (double.IsNaN(n) || double.IsInfinity(n)) throw new ArgumentException("مقاس الطابعة غير صالح.");
            if (width<=0 || height<=0 || originX<0 || originY<0 || originX>=width || originY>=height
                || printableWidth<=0 || printableHeight<=0) throw new ArgumentException("مساحة الطباعة غير صالحة.");
            printableWidth=Math.Min(printableWidth,width-originX);
            printableHeight=Math.Min(printableHeight,height-originY);
            var margin=Math.Min(18,printableWidth/16);
            var content=printableWidth-2*margin;
            return new ReceiptPageLayout { Width=width,Height=height,Left=originX+margin,Top=originY+margin,
                Right=width-originX-printableWidth+margin,Bottom=height-originY-printableHeight+margin,
                ContentWidth=content,FontSize=Math.Max(8,Math.Min(12,content/24)),QrSide=Math.Min(170,content) };
        }
    }

    public sealed class ProductRow
    {
        public long Id { get; set; }
        public string Sku { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
        public string Barcode { get; set; }
        public string PreferredSupplier { get; set; }
        public long SalePriceCents { get; set; }
        public long LastCostCents { get; set; }
        public long StockMilli { get; set; }
        public bool IsCountable { get; set; }
        public long ReorderMilli { get; set; }
        public string ReorderText { get { return Amounts.Qty(ReorderMilli) + " " + Unit; } }
        public bool NeedsRestock { get { return ReorderMilli > 0 && StockMilli <= ReorderMilli; } }
        public string StockStatus { get { return NeedsRestock ? (StockMilli == 0 ? "نفد" : "قرب ينفد") : "متوفر"; } }
        public string DisplayName { get { return Name + " (" + Sku + ")"; } }
        public string SalePriceText { get { return Amounts.Price(SalePriceCents); } }
        public string StockText { get { return Amounts.Qty(StockMilli) + " " + Unit; } }
    }

    public sealed class ProductUnitRow
    {
        public long Id { get; set; }
        public long ProductId { get; set; }
        public string Name { get; set; }
        public long FactorMilli { get; set; }
        public long SalePriceCents { get; set; }
        public long SemiPriceCents { get; set; }
        public long WholesalePriceCents { get; set; }

        public long PriceForLevel(string level)
        {
            if (level == "RETAIL") return SalePriceCents;
            if (level == "SEMI") return SemiPriceCents;
            if (level == "WHOLESALE") return WholesalePriceCents;
            throw new ArgumentException("مستوى البيع غير صالح.");
        }
        public string FactorText { get { return Amounts.Qty(FactorMilli) + " من الوحدة الأساسية"; } }
        public string PriceText { get { return Amounts.Price(SalePriceCents); } }
        public string DisplayName { get { return Name + " (" + FactorText + ")"; } }
    }
    public sealed class LowStockRow
    {
        public long ProductId { get; set; }
        public string Sku { get; set; }
        public string ProductName { get; set; }
        public string Unit { get; set; }
        public long StockMilli { get; set; }
        public long ReorderMilli { get; set; }
        public string Status { get { return StockMilli == 0 ? "نفد المخزون" : "قرب النفاد"; } }
        public string StockText { get { return Amounts.Qty(StockMilli) + " " + Unit; } }
        public string LimitText { get { return Amounts.Qty(ReorderMilli) + " " + Unit; } }
    }

    public sealed class PartyRow
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string Phone { get; set; }
    }

    public sealed class DraftLine
    {
        public long ProductId { get; set; }
        public string ProductName { get; set; }
        public long QuantityMilli { get; set; }
        public long PriceCents { get; set; } // Price of the selected package, NOT price per base unit.
        public string PriceLevel { get; set; } = "RETAIL"; // Used on sales, ignored for purchases.
        public string PriceLevelText
        {
            get { return PriceLevel == "WHOLESALE" ? "جملة" :
                PriceLevel == "SEMI" ? "نص قطاعي" : "قطاعي"; }
        }
        public long ProductUnitId { get; set; }
        public string UnitName { get; set; }
        public long UnitFactorMilli { get; set; }
        public long EnteredQuantityMilli { get; set; }
        public string EnteredQtyText { get { return Amounts.Qty(EnteredQuantityMilli) + " " + UnitName; } }
        public long LineTotalCents { get { return Amounts.LineTotal(EnteredQuantityMilli, PriceCents); } }
        public string QtyText { get { return Amounts.Qty(QuantityMilli); } }
        public string PriceText { get { return Amounts.Price(PriceCents); } }
        public string TotalText { get { return Amounts.Price(LineTotalCents); } }
    }

    public sealed class DocumentRow
    {
        public long Id { get; set; }
        public string Code { get; set; }
        public string Type { get; set; }
        public string Party { get; set; }
        public string Employee { get; set; }
        public string CreatedUtc { get; set; }
        public long TotalCents { get; set; }
        public string TotalText { get { return Amounts.Price(TotalCents); } }
        public string TypeText { get { return Type == "SALE" ? "بيع" : "شراء"; } }
    }

    public sealed class MovementRow
    {
        public string DocumentCode { get; set; }
        public string ProductName { get; set; }
        public long DeltaMilli { get; set; }
        public string CreatedUtc { get; set; }
        public string DeltaText { get { return Amounts.Qty(DeltaMilli); } }
    }
}
