using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using OfflineStore.Models;
using OfflineStore.Services;
using QRCoder;

namespace OfflineStore
{
    /// <summary>Print, preview and export the exact posted-sale money/quantity snapshots.</summary>
    public sealed class SaleReceiptWindow : Window
    {
        private readonly SaleReceiptData _receipt;
        private readonly string _lanUrl;
        private readonly byte[] _qr;
        private Border _paper;

        public SaleReceiptWindow(SaleReceiptData receipt, string lanUrl)
        {
            _receipt = receipt ?? throw new ArgumentNullException("receipt");
            _lanUrl = lanUrl;
            var qrText = !string.IsNullOrWhiteSpace(lanUrl) ? lanUrl :
                "OfflineStore | " + receipt.Code + " | " + Amounts.Price(receipt.TotalCents) + " EGP";
            using(var generator = new QRCodeGenerator())
            using(var qrData = generator.CreateQrCode(qrText,QRCodeGenerator.ECCLevel.M))
            using(var image = new PngByteQRCode(qrData))
                _qr = image.GetGraphic(9);
            Build();
        }

        private static TextBlock Label(string value,int font=15,bool strong=false)
        {
            return new TextBlock
            {
                Text=value, FontSize=font, FontWeight=strong ? FontWeights.SemiBold : FontWeights.Normal,
                TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,4,0,4),
                Foreground=Brushes.Black, FlowDirection=FlowDirection.RightToLeft
            };
        }

        private static BitmapSource QrBitmap(byte[] bytes)
        {
            var bitmap=new BitmapImage();
            using(var stream=new MemoryStream(bytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption=BitmapCacheOption.OnLoad;
                bitmap.StreamSource=stream;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }

        private static Button Action(string title,RoutedEventHandler click)
        {
            var button=new Button
            {
                Content=title,Margin=new Thickness(5),Padding=new Thickness(12,8,12,8),
                MinHeight=38,Cursor=System.Windows.Input.Cursors.Hand
            };
            button.Click+=click;
            return button;
        }

        private void Build()
        {
            Title="فاتورة بيع " + _receipt.Code + " | عرض وطباعة";
            Width=680; Height=760; MinWidth=540; MinHeight=550;
            WindowStartupLocation=WindowStartupLocation.CenterOwner;
            FlowDirection=FlowDirection.LeftToRight;
            Background=new SolidColorBrush(Color.FromRgb(237,243,250));
            var frame=new DockPanel { LastChildFill=true, Background=new SolidColorBrush(Color.FromRgb(237,243,250)) };
            var buttons=new WrapPanel { Margin=new Thickness(12) };
            buttons.Children.Add(Action("طباعة الفاتورة",PrintClick));
            buttons.Children.Add(Action("حفظ صورة PNG",SavePngClick));
            buttons.Children.Add(Action("حفظ صورة SVG",SaveSvgClick));
            if(!string.IsNullOrWhiteSpace(_lanUrl))
                buttons.Children.Add(Action("نسخ رابط الهاتف",CopyLinkClick));
            DockPanel.SetDock(buttons,Dock.Top);frame.Children.Add(buttons);

            var note=Label(string.IsNullOrWhiteSpace(_lanUrl)
                ? "لا توجد شبكة محلية متاحة. QR يحتوي على رقم الفاتورة والإجمالي فقط؛ لا يستطيع فتح صورتها على الهاتف."
                : "لرؤية صورة الفاتورة على الهاتف: وصّل الهاتف والكمبيوتر بنفس شبكة Wi-Fi المحلية، وامسح QR. لا يحتاج ذلك إلى الإنترنت. قد يلزم السماح للتطبيق بجدار حماية Windows.",12);
            note.Margin=new Thickness(15,0,15,8);
            DockPanel.SetDock(note,Dock.Top);frame.Children.Add(note);

            var content=new StackPanel { FlowDirection=FlowDirection.RightToLeft };
            content.Children.Add(Label("OfflineStore",28,true));
            content.Children.Add(Label("فاتورة بيع معتمدة",19,true));
            content.Children.Add(Label("رقم الفاتورة: "+_receipt.Code,17,true));
            content.Children.Add(Label("التوقيت UTC: "+_receipt.CreatedUtc,13));
            content.Children.Add(Label("الموظف: "+_receipt.Employee,13));
            content.Children.Add(new Separator { Margin=new Thickness(0,10,0,10) });
            foreach(var line in _receipt.Lines)
            {
                var row=new StackPanel { Margin=new Thickness(0,5,0,8) };
                row.Children.Add(Label(line.ProductName,16,true));
                row.Children.Add(Label(line.QuantityText+" × "+Amounts.Price(line.UnitPriceCents)
                    +" = "+Amounts.Price(line.LineTotalCents)+" جنيه" +
                    (line.PriceLevelText.Length==0 ? "" : " — "+line.PriceLevelText),14));
                content.Children.Add(row);
                content.Children.Add(new Separator());
            }
            content.Children.Add(Label("الإجمالي: "+Amounts.Price(_receipt.TotalCents)+" جنيه",22,true));
            content.Children.Add(Label("امسح QR لعرض صورة الفاتورة على الهاتف",13));
            var qr=new System.Windows.Controls.Image
            {
                Source=QrBitmap(_qr),Width=215,Height=215,Stretch=Stretch.Uniform,
                FlowDirection=FlowDirection.LeftToRight,Margin=new Thickness(0,12,0,12)
            };
            content.Children.Add(qr);
            if(!string.IsNullOrWhiteSpace(_lanUrl))
                content.Children.Add(Label("رابط محلي فقط: "+_lanUrl,11));
            if(_receipt.Lines.Exists(x=>x.LegacyName))
                content.Children.Add(Label("تنبيه: أسماء الأصناف في الفواتير القديمة تم التقاطها وقت الترقية.",12));
            _paper=new Border
            {
                Width=545,Padding=new Thickness(24),Background=Brushes.White,
                BorderThickness=new Thickness(1),BorderBrush=Brushes.LightGray,
                HorizontalAlignment=HorizontalAlignment.Center,Child=content,
                Margin=new Thickness(10)
            };
            var scroll=new ScrollViewer
            {
                VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
                Content=_paper
            };
            frame.Children.Add(scroll);
            Content=frame;
        }

        private void CopyLinkClick(object sender,RoutedEventArgs e)
        {
            Clipboard.SetText(_lanUrl);
            MessageBox.Show(this,"تم نسخ رابط الشبكة المحلية.","رابط الفاتورة",
                MessageBoxButton.OK,MessageBoxImage.Information);
        }

        private void SaveSvgClick(object sender,RoutedEventArgs e)
        {
            var dialog=new SaveFileDialog
            {
                Title="حفظ صورة الفاتورة SVG",Filter="SVG image (*.svg)|*.svg",
                FileName="Invoice-"+_receipt.Code+".svg",OverwritePrompt=true
            };
            if(dialog.ShowDialog(this)==true)
                File.WriteAllBytes(dialog.FileName,SaleReceiptSvg.Render(_receipt));
        }

        private void SavePngClick(object sender,RoutedEventArgs e)
        {
            var dialog=new SaveFileDialog
            {
                Title="حفظ صورة الفاتورة PNG",Filter="PNG image (*.png)|*.png",
                FileName="Invoice-"+_receipt.Code+".png",OverwritePrompt=true
            };
            if(dialog.ShowDialog(this)!=true)return;
            var width=545.0;
            _paper.Measure(new Size(width,double.PositiveInfinity));
            var height=Math.Ceiling(_paper.DesiredSize.Height);
            if(height>15000)
            {
                MessageBox.Show(this,"الفاتورة طويلة جدًا لصورة PNG واحدة. استخدم طباعة الفاتورة أو SVG.");
                return;
            }
            _paper.Arrange(new Rect(0,0,width,height));
            var bmp=new RenderTargetBitmap((int)width,(int)height,96,96,PixelFormats.Pbgra32);
            bmp.Render(_paper);
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using(var file=File.Create(dialog.FileName))encoder.Save(file);
        }

        private FlowDocument Printable(ReceiptPageLayout layout)
        {
            var doc=new FlowDocument
            {
                FlowDirection=FlowDirection.RightToLeft,
                FontFamily=new FontFamily("Segoe UI"),
                FontSize=layout.FontSize,PageWidth=layout.Width,PageHeight=layout.Height,
                PagePadding=new Thickness(layout.Left,layout.Top,layout.Right,layout.Bottom),
                ColumnWidth=layout.ContentWidth,ColumnGap=0
            };
            doc.Blocks.Add(new Paragraph(new Run("OfflineStore — فاتورة بيع"))
                { FontSize=21,FontWeight=FontWeights.Bold });
            doc.Blocks.Add(new Paragraph(new Run("رقم الفاتورة: "+_receipt.Code)));
            doc.Blocks.Add(new Paragraph(new Run("التاريخ UTC: "+_receipt.CreatedUtc)));
            doc.Blocks.Add(new Paragraph(new Run("الموظف: "+_receipt.Employee)));
            foreach(var line in _receipt.Lines)
            {
                doc.Blocks.Add(new Paragraph(new Run(line.ProductName))
                    { FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,5,0,0) });
                doc.Blocks.Add(new Paragraph(new Run(
                    line.QuantityText+" × "+Amounts.Price(line.UnitPriceCents)+" = "+
                    Amounts.Price(line.LineTotalCents)+" جنيه"))
                    { Margin=new Thickness(0,0,0,7) });
            }
            doc.Blocks.Add(new Paragraph(new Run(
                "الإجمالي: "+Amounts.Price(_receipt.TotalCents)+" جنيه"))
                { FontSize=18,FontWeight=FontWeights.Bold });
            var image=new System.Windows.Controls.Image
            {
                Source=QrBitmap(_qr),Width=layout.QrSide,Height=layout.QrSide,
                FlowDirection=FlowDirection.LeftToRight
            };
            doc.Blocks.Add(new BlockUIContainer(image));
            doc.Blocks.Add(new Paragraph(new Run(string.IsNullOrWhiteSpace(_lanUrl)
                ? "QR يعرض بيانات الفاتورة المختصرة — صورة الهاتف تتطلب شبكة محلية."
                : "امسح QR لعرض صورة الفاتورة على نفس شبكة Wi-Fi المحلية.")));
            return doc;
        }

        private void PrintClick(object sender,RoutedEventArgs e)
        {
            try
            {
            var dialog=new PrintDialog();
            if(dialog.ShowDialog()==true)
            {
                var caps=dialog.PrintQueue.GetPrintCapabilities(dialog.PrintTicket);
                var media=dialog.PrintTicket.PageMediaSize;
                var area=caps.PageImageableArea;
                var layout=ReceiptPageLayout.ForPrinter(
                    media?.Width ?? dialog.PrintableAreaWidth, media?.Height ?? dialog.PrintableAreaHeight,
                    area?.OriginWidth ?? 0, area?.OriginHeight ?? 0,
                    area?.ExtentWidth ?? dialog.PrintableAreaWidth, area?.ExtentHeight ?? dialog.PrintableAreaHeight);
                var doc=Printable(layout);
                dialog.PrintDocument(((IDocumentPaginatorSource)doc).DocumentPaginator,
                    "OfflineStore Invoice "+_receipt.Code);
            }
            }
            catch(Exception ex)
            {
                MessageBox.Show(this,"تعذرت الطباعة: "+ex.Message,"الطباعة",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }
    }
}
