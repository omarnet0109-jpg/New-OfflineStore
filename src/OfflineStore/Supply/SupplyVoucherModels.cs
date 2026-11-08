using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using OfflineStore.Models;

namespace OfflineStore.Supply
{
    // All money fields are INTEGER piastres, quantities INTEGER milli-units.
    public sealed class SupplyVoucherLine
    {
        public long DetailID { get; set; }
        public long ProductID { get; set; }
        public long ProductUnitID { get; set; }
        public string ItemCode { get; set; }
        public string ItemName { get; set; }
        public string Unit { get; set; }
        public long UnitFactorMilli { get; set; }
        public long QuantityMilli { get; set; }
        public long UnitCostCents { get; set; }
        public string DiscountType { get; set; } = "AMOUNT";
        // AMOUNT: piastres; PERCENT: basis points (14.25% => 1425).
        public long DiscountValue { get; set; }
        public long BaseQuantityMilli { get; internal set; }
        public long GrossTotalCents { get; internal set; }
        public long DiscountCents { get; internal set; }
        public long LineTotalCents { get; internal set; }
    }

    public sealed class SupplyVoucherDraft
    {
        public long VoucherID { get; set; }
        public string VoucherCode { get; set; }
        public DateTime VoucherDate { get; set; } = DateTime.Today;
        public long SupplierID { get; set; }
        public string SupplierInvoiceNo { get; set; } = "";
        public DateTime SupplierInvoiceDate { get; set; } = DateTime.Today;
        public string PaymentType { get; set; } = "CASH";
        public long TaxRateBps { get; set; }
        public long HeaderDiscountCents { get; set; }
        public long PaidAmountCents { get; set; }
        public string Notes { get; set; } = "";
        public string State { get; set; } = "DRAFT";
        public List<SupplyVoucherLine> Lines { get; set; } = new List<SupplyVoucherLine>();
    }

    public sealed class SupplyVoucherTotals
    {
        public long SubTotalCents { get; set; }
        public long ItemDiscountCents { get; set; }
        public long HeaderDiscountCents { get; set; }
        public long TotalDiscountCents { get; set; }
        public long TaxAmountCents { get; set; }
        public long GrandTotalCents { get; set; }
        public long PaidAmountCents { get; set; }
        public long RemainingAmountCents { get; set; }
        public string TotalWordsAr { get; set; }
    }

    public static class SupplyVoucherCalculator
    {
        private static long Rounded(decimal number)
        {
            return checked((long)decimal.Round(number, 0, MidpointRounding.AwayFromZero));
        }

        public static SupplyVoucherTotals Calculate(SupplyVoucherDraft draft)
        {
            if (draft == null || draft.Lines == null || draft.Lines.Count == 0)
                throw new ArgumentException("أضف صنفًا واحدًا على الأقل.");
            if (draft.Lines.Count > 500)
                throw new ArgumentException("الحد الأقصى 500 صنف في سند التوريد.");
            if (draft.SupplierID <= 0) throw new ArgumentException("اختر المورد.");
            if (draft.VoucherDate.Date < draft.SupplierInvoiceDate.Date)
                throw new ArgumentException("تاريخ فاتورة المورد لا يجوز أن يأتي بعد تاريخ التوريد.");
            if (draft.PaymentType != "CASH" && draft.PaymentType != "CREDIT")
                throw new ArgumentException("حدد نقدي أو آجل.");
            if (draft.TaxRateBps < 0 || draft.TaxRateBps > 10000)
                throw new ArgumentException("نسبة الضريبة يجب أن تكون من 0 إلى 100%.");
            long gross = 0, itemsDiscount = 0, netItems = 0;
            foreach (var line in draft.Lines)
            {
                if (line.ProductID <= 0 || line.ProductUnitID <= 0 ||
                    line.UnitFactorMilli < 1000 || line.UnitFactorMilli % 1000 != 0 ||
                    line.QuantityMilli <= 0 || line.QuantityMilli > 1000000000 ||
                    line.UnitCostCents <= 0 || string.IsNullOrWhiteSpace(line.Unit))
                    throw new ArgumentException("كل صنف يحتاج وحدة وكمية وسعر شراء أكبر من صفر.");
                line.BaseQuantityMilli = Amounts.BaseQuantity(line.QuantityMilli, line.UnitFactorMilli);
                line.GrossTotalCents = Amounts.LineTotal(line.QuantityMilli, line.UnitCostCents);
                if (line.DiscountType == "AMOUNT")
                    line.DiscountCents = line.DiscountValue;
                else if (line.DiscountType == "PERCENT" &&
                         line.DiscountValue >= 0 && line.DiscountValue <= 10000)
                    line.DiscountCents = Rounded((decimal)line.GrossTotalCents * line.DiscountValue / 10000m);
                else throw new ArgumentException("نوع خصم الصنف أو نسبته غير صالحة.");
                if (line.DiscountCents < 0 || line.DiscountCents >= line.GrossTotalCents)
                    throw new ArgumentException("خصم الصنف لا يجوز أن يساوي أو يتجاوز قيمته.");
                line.LineTotalCents = checked(line.GrossTotalCents - line.DiscountCents);
                gross = checked(gross + line.GrossTotalCents);
                itemsDiscount = checked(itemsDiscount + line.DiscountCents);
                netItems = checked(netItems + line.LineTotalCents);
            }
            if (draft.HeaderDiscountCents < 0 || draft.HeaderDiscountCents >= netItems)
                throw new ArgumentException("خصم السند يجب أن يكون أقل من صافي الأصناف.");
            long taxable = checked(netItems - draft.HeaderDiscountCents);
            long tax = Rounded((decimal)taxable * draft.TaxRateBps / 10000m);
            long grand = checked(taxable + tax);
            long paid = draft.PaymentType == "CASH" ? grand : draft.PaidAmountCents;
            if (paid < 0 || paid > grand)
                throw new ArgumentException("المبلغ المدفوع يجب ألا يتجاوز إجمالي السند.");
            return new SupplyVoucherTotals {
                SubTotalCents=gross, ItemDiscountCents=itemsDiscount,
                HeaderDiscountCents=draft.HeaderDiscountCents,
                TotalDiscountCents=checked(itemsDiscount + draft.HeaderDiscountCents),
                TaxAmountCents=tax, GrandTotalCents=grand,
                PaidAmountCents=paid, RemainingAmountCents=grand-paid,
                TotalWordsAr=ArabicMoneyWords.EgyptianPounds(grand)
            };
        }

        // A moving-average remains sub-cent precise; no floating point in inventory.
        // existingStockMilli excludes the draft's movements, so include staged receipts
        // from this SAME transaction when multiple lines contain the same product.
        public static long WeightedAverageMilliCents(long existingStockMilli,
            long oldAverageMilliCents, long receivedMilli, long netReceiptValueCents)
        {
            if (existingStockMilli < 0 || oldAverageMilliCents < 0 ||
                receivedMilli <= 0 || netReceiptValueCents < 0)
                throw new ArgumentException("قيم متوسط التكلفة غير صالحة.");
            decimal oldValue = existingStockMilli == 0 ? 0 :
                (decimal)existingStockMilli * oldAverageMilliCents;
            decimal newValue = (decimal)netReceiptValueCents * 1000000m;
            return Rounded((oldValue + newValue) / (existingStockMilli + receivedMilli));
        }

        // Allocate capitalized invoice tax cumulatively; the exact header tax is conserved.
        public static IList<long> AllocateCapitalizedTax(IList<long> netCosts,long tax)
        {
            if(netCosts==null || netCosts.Count==0 || tax<0 || netCosts.Any(x=>x<0))throw new ArgumentException("أساس توزيع الضريبة غير صالح.");
            long total=netCosts.Aggregate(0L,(sum,x)=>checked(sum+x));
            if(total<=0)throw new ArgumentException("قيمة التوريد الصافية يجب أن تكون موجبة.");
            var result=new List<long>();long assigned=0,cumulative=0;
            for(int i=0;i<netCosts.Count;i++)
            {
                cumulative=checked(cumulative+netCosts[i]);
                long target=i==netCosts.Count-1?tax:checked((long)decimal.Truncate((decimal)tax*cumulative/total));
                result.Add(checked(target-assigned));assigned=target;
            }
            return result;
        }
        // Allocates an overall discount proportionally with an exact final remainder.
        public static IList<long> AllocateHeaderDiscount(IList<SupplyVoucherLine> lines, long totalDiscount)
        {
            var result = new List<long>();
            long net = lines.Aggregate(0L, (sum, item) => checked(sum + item.LineTotalCents));
            long assigned = 0;
            long cumulative = 0;
            for (int i=0; i<lines.Count; i++)
            {
                cumulative=checked(cumulative+lines[i].LineTotalCents);
                // Cumulative proportional allocation prevents tiny final lines from
                // receiving a discount larger than their own net cost.
                long target=i==lines.Count-1?totalDiscount:
                    checked((long)decimal.Truncate((decimal)totalDiscount*cumulative/net));
                long value=checked(target-assigned);
                if(value<0 || value>lines[i].LineTotalCents)
                    throw new InvalidOperationException("توزيع الخصم تجاوز قيمة صنف.");
                result.Add(value);
                assigned=target;
            }
            if (assigned != totalDiscount)
                throw new InvalidOperationException("تعذر توزيع خصم السند.");
            return result;
        }
    }

    public static class ArabicMoneyWords
    {
        private static readonly string[] Ones = {
            "","واحد","اثنان","ثلاثة","أربعة","خمسة","ستة","سبعة","ثمانية","تسعة",
            "عشرة","أحد عشر","اثنا عشر","ثلاثة عشر","أربعة عشر","خمسة عشر",
            "ستة عشر","سبعة عشر","ثمانية عشر","تسعة عشر"
        };
        private static readonly string[] Tens = {
            "","","عشرون","ثلاثون","أربعون","خمسون","ستون","سبعون","ثمانون","تسعون"
        };
        private static readonly string[] Hundreds = {
            "","مائة","مائتان","ثلاثمائة","أربعمائة","خمسمائة",
            "ستمائة","سبعمائة","ثمانمائة","تسعمائة"
        };
        private static string Three(int n)
        {
            if (n==0) return "";
            var parts = new List<string>();
            int h=n/100, rest=n%100;
            if (h>0) parts.Add(Hundreds[h]);
            if (rest>0)
                parts.Add(rest<20 ? Ones[rest] :
                    (rest%10>0 ? Ones[rest%10]+" و"+Tens[rest/10] : Tens[rest/10]));
            return string.Join(" و", parts);
        }
        private static string Whole(long n)
        {
            if (n==0) return "صفر";
            var groups = new [] {
                new []{"","",""},
                new []{"ألف","ألفان","آلاف"},
                new []{"مليون","مليونان","ملايين"},
                new []{"مليار","ملياران","مليارات"},
                new []{"تريليون","تريليونان","تريليونات"}
            };
            var result = new List<string>();
            int group=0;
            while(n>0)
            {
                if(group>=groups.Length) throw new ArgumentException("المبلغ كبير جدًا للتفقيط.");
                int value=(int)(n%1000);
                if(value>0)
                {
                    string text;
                    if(group==0) text=Three(value);
                    else if(value==1) text=groups[group][0];
                    else if(value==2) text=groups[group][1];
                    else if(value>=3 && value<=10) text=Three(value)+" "+groups[group][2];
                    else text=Three(value)+" "+groups[group][0];
                    result.Insert(0,text);
                }
                n/=1000; group++;
            }
            return string.Join(" و",result);
        }
        public static string EgyptianPounds(long totalCents)
        {
            if (totalCents < 0) throw new ArgumentException("مبلغ سالب.");
            long pounds=totalCents/100, piasters=totalCents%100;
            var text=Whole(pounds)+" جنيه مصري";
            if (piasters>0) text+=" و"+Whole(piasters)+" قرش";
            return "فقط "+text+" لا غير";
        }
    }
}
