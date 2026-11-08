using System;
using OfflineStore.Models;
using OfflineStore.Supply;
using System.Collections.Generic;

internal static class Program
{
    private static int _tests;
    private static void Equal(long expected, long actual, string name)
    {
        _tests++;
        if (expected != actual) throw new Exception(name + ": expected " + expected + " got " + actual);
    }
    private static void Reject(Action test, string name)
    {
        _tests++;
        try { test(); }
        catch (ArgumentException) { return; }
        throw new Exception(name + ": bad input was accepted");
    }
    private static void Main()
    {
        Equal(0,Amounts.ProportionalReturn(1,0,1,3),"first tiny return slice");
        Equal(1,Amounts.ProportionalReturn(1,1,1,3),"second tiny return slice");
        Equal(0,Amounts.ProportionalReturn(1,2,1,3),"last slice conserves exact original amount");
        Equal(333333,Amounts.ProportionalReturn(1000000,0,1000,3000),"large proportional return");
        Reject(()=>Amounts.ProportionalReturn(100,900,200,1000),"cumulative overreturn");
        var allocatedTax=SupplyVoucherCalculator.AllocateCapitalizedTax(new long[] {1,2,97},14);
        Equal(0,allocatedTax[0],"tiny line tax");Equal(0,allocatedTax[1],"second tiny line tax");Equal(14,allocatedTax[2],"tax remainder conserved");
        var splitTax=SupplyVoucherCalculator.AllocateCapitalizedTax(new long[] {5000,5000},1400);
        Equal(700,splitTax[0],"first purchase capitalized tax");Equal(700,splitTax[1],"second purchase capitalized tax");
        foreach (double mm in new [] {58.0,80.0,210.0})
        {
            double width=mm*96/25.4;
            var layout=ReceiptPageLayout.ForPrinter(width,1100,4,5,width-8,1090);
            if(layout.Left<4 || layout.Right<4 || layout.QrSide>layout.ContentWidth
                || layout.ContentWidth+layout.Left+layout.Right>width+0.001)
                throw new Exception("Printer content exceeds imageable area: "+mm);
        }
        Reject(() => ReceiptPageLayout.ForPrinter(0,100,0,0,20,20),"invalid printer media");
        Equal(100, Amounts.CostTotal(120000,833), "fractional carton total rounded once");
        Equal(833000, Amounts.CostTotal(1000000000,833), "large fractional cost retains precision");
        Equal(1, Amounts.CostTotal(1000,500), "half-cent cost rounds away from zero");
        Reject(() => Amounts.CostTotal(0,100), "zero cost quantity");
        Reject(() => Amounts.CostTotal(1000,-1), "negative cost basis");
        Equal(1525, Amounts.ToCents("15.25", "price"), "decimal money");
        Equal(1525, Amounts.ToCents("١٥٫٢٥", "price"), "Arabic-Indic money");
        Equal(2500, Amounts.ToMilli("۲٫۵", "quantity"), "Persian quantity");
        Equal(0, Amounts.ToMilli("٠", "reorder", true), "Arabic zero reorder");
        Equal(0, Amounts.ToMilli("0.0", "reorder", true), "decimal zero reorder");
        Equal(0, Amounts.ToMilli("۰٫۰۰", "reorder", true), "Persian zero reorder");
        Reject(() => Amounts.ToMilli("-1", "reorder", true), "negative reorder");
        Equal(96000, Amounts.BaseQuantity(2000, 48000), "two cartons are 96 pieces");
        // User-reported case: 200 base units cannot cover 2 packs of 120.
        Equal(240000, Amounts.BaseQuantity(Amounts.ToMilli("٢","الكمية"),120000),
              "two 120-piece packages need 240 base pieces");
        Equal(200000/120000,1,"only one complete 120-piece pack is available");
        _tests++;
        if (!(Amounts.BaseQuantity(2000,120000)>200000))
            throw new Exception("false sale acceptance for insufficient stock");
        _tests++;
        if (Amounts.BaseQuantity(2000,1000)>200000)
            throw new Exception("two single pieces incorrectly rejected against 200 stock");

        Equal(51000, Amounts.BaseQuantity(1000, 48000) + Amounts.BaseQuantity(3000, 1000), "carton plus three pieces");
        Equal(64500, Amounts.LineTotal(1000, 60000) + Amounts.LineTotal(3000, 1500), "mixed unit total");
        Equal(51, Amounts.LineTotal(500, 101), "half-cent round away from zero");
        Reject(() => Amounts.ToCents("1.001", "price"), "more than two money decimals");
        Reject(() => Amounts.ToMilli("0", "quantity"), "zero quantity");
        Reject(() => Amounts.ToCents("-1", "price"), "negative price");
        Reject(() => Amounts.BaseQuantity(1000, 1500), "invalid factor");
        var voucher=new SupplyVoucherDraft {
            SupplierID=1, PaymentType="CASH",TaxRateBps=1400,
            HeaderDiscountCents=2000,
            Lines=new List<SupplyVoucherLine> {
                new SupplyVoucherLine { ProductID=1,ProductUnitID=1,Unit="قطعة",
                    UnitFactorMilli=1000,QuantityMilli=2000,UnitCostCents=10000,
                    DiscountType="PERCENT",DiscountValue=1000 },
                new SupplyVoucherLine { ProductID=2,ProductUnitID=2,Unit="قطعة",
                    UnitFactorMilli=1000,QuantityMilli=3000,UnitCostCents=5000,
                    DiscountType="AMOUNT",DiscountValue=1000 }
            }
        };
        var t=SupplyVoucherCalculator.Calculate(voucher);
        Equal(35000,t.SubTotalCents,"receipt subtotal");
        Equal(3000,t.ItemDiscountCents,"receipt line discounts");
        Equal(5000,t.TotalDiscountCents,"receipt total discounts");
        Equal(4200,t.TaxAmountCents,"receipt VAT 14%");
        Equal(34200,t.GrandTotalCents,"receipt grand total");
        Equal(34200,t.PaidAmountCents,"cash fully paid");
        Equal(0,t.RemainingAmountCents,"cash no credit");
        var share=SupplyVoucherCalculator.AllocateHeaderDiscount(voucher.Lines,2000);
        Equal(1125,share[0],"receipt header discount line 1");
        Equal(875,share[1],"receipt header discount line 2");
        Equal(150000,SupplyVoucherCalculator.WeightedAverageMilliCents(
            10000,100000,10000,2000),"weighted moving average");
        Equal(200000,SupplyVoucherCalculator.WeightedAverageMilliCents(
            0,0,10000,2000),"first receipt cost");
        var tinyLines=new List<SupplyVoucherLine>();
        for(int i=0;i<10;i++)tinyLines.Add(new SupplyVoucherLine {LineTotalCents=1});
        var safeAllocation=SupplyVoucherCalculator.AllocateHeaderDiscount(tinyLines,9);
        Equal(9,System.Linq.Enumerable.Sum(safeAllocation),"tiny-line proportional discount total");
        foreach(var discount in safeAllocation)
        {
            _tests++;
            if(discount<0||discount>1)throw new Exception("Tiny line overdiscounted.");
        }
        voucher.PaymentType="CREDIT";voucher.PaidAmountCents=10000;
        t=SupplyVoucherCalculator.Calculate(voucher);
        Equal(24200,t.RemainingAmountCents,"credit remainder");
        voucher.SupplierInvoiceDate=voucher.VoucherDate.AddDays(1);
        Reject(()=>SupplyVoucherCalculator.Calculate(voucher),"future supplier invoice");
        voucher.SupplierInvoiceDate=voucher.VoucherDate;
        voucher.PaidAmountCents=40000;
        Reject(()=>SupplyVoucherCalculator.Calculate(voucher),"overpayment");
        voucher.PaidAmountCents=0;
        voucher.Lines[0].DiscountValue=11000;
        Reject(()=>SupplyVoucherCalculator.Calculate(voucher),"discount above 100%");
        voucher.Lines[0].DiscountValue=1000;
        voucher.Lines[0].QuantityMilli=0;
        Reject(()=>SupplyVoucherCalculator.Calculate(voucher),"zero quantity");
        voucher.Lines[0].QuantityMilli=2000;
        if (!ArabicMoneyWords.EgyptianPounds(34200).Contains("جنيه"))
            throw new Exception("Arabic words failed");
        _tests++;
        Console.WriteLine("Domain calculations passed: " + _tests);
    }
}
