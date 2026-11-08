using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace OfflineStore.Appearance
{
    /// <summary>
    /// The Windows ICO contains 16,24,32,48,64 and 128 pixel frames.
    /// WPF's default URI decoder may display the smallest first frame;
    /// deliberately pick the largest frame so the existing official shop
    /// icon stays crisp in the blue login and brand rails.
    /// </summary>
    public static class BrandAssets
    {
        private static readonly Lazy<ImageSource> Image =
            new Lazy<ImageSource>(LoadStoreIcon);
        public static ImageSource StoreIcon { get { return Image.Value; } }

        private static ImageSource LoadStoreIcon()
        {
            var uri = new Uri("pack://application:,,,/OfflineStore;component/Assets/OfflineStore.ico",
                UriKind.Absolute);
            var resource = Application.GetResourceStream(uri);
            if (resource == null)
                throw new InvalidOperationException("أيقونة OfflineStore غير موجودة في الموارد.");
            using (resource.Stream)
            {
                var decoder = new IconBitmapDecoder(resource.Stream,
                    BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                var frame = decoder.Frames.OrderByDescending(
                    x => (long)x.PixelWidth * x.PixelHeight).First();
                if (frame.CanFreeze) frame.Freeze();
                return frame;
            }
        }
    }
}
