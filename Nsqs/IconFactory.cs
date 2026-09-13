using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace Nsqs
{
    public static class IconFactory
    {
        private const int DesignSize = 32;

        public static Icon CreateTrayIcon() => CreateIconFromBitmap(DrawBitmap(DesignSize));

        public static BitmapSource CreateWindowIconSource()
        {
            using var bitmap = DrawBitmap(DesignSize);
            using var pngStream = new MemoryStream();
            bitmap.Save(pngStream, ImageFormat.Png);
            pngStream.Seek(0, SeekOrigin.Begin);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = pngStream;
            image.EndInit();
            image.Freeze();
            return image;
        }

        public static void SaveApplicationIconFile(string path)
        {
            var sizes = new[] { 16, 32, 48, 256 };
            var pngImages = sizes.Select(size =>
            {
                using var bitmap = DrawBitmap(size);
                using var stream = new MemoryStream();
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }).ToList();

            using var icoStream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new BinaryWriter(icoStream);

            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)pngImages.Count);

            var offset = 6 + 16 * pngImages.Count;
            foreach (var (size, index) in sizes.Select((size, index) => (size, index)))
            {
                var pngBytes = pngImages[index];
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)(size >= 256 ? 0 : size));
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(pngBytes.Length);
                writer.Write(offset);
                offset += pngBytes.Length;
            }

            foreach (var pngBytes in pngImages)
                writer.Write(pngBytes);
        }

        private static Bitmap DrawBitmap(int size)
        {
            var accent = ThemeHelper.GetSystemAccentColor();
            var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bitmap);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(255, accent.R, accent.G, accent.B));
            g.FillEllipse(bg, 0, 0, size, size);

            float fontSize = Math.Max(size * (14f / DesignSize), 6f);
            using var font = new Font("Segoe MDL2 Assets", fontSize, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            var glyph = "\uE8B7"; // Folder
            var textSize = g.MeasureString(glyph, font);
            var x = (size - textSize.Width) / 2f;
            var y = (size - textSize.Height) / 2f;
            g.DrawString(glyph, font, fg, x, y);

            return bitmap;
        }

        private static Icon CreateIconFromBitmap(Bitmap bitmap)
        {
            using (bitmap)
            {
                using var pngStream = new MemoryStream();
                bitmap.Save(pngStream, ImageFormat.Png);
                var pngBytes = pngStream.ToArray();

                using var icoStream = new MemoryStream();
                using var writer = new BinaryWriter(icoStream);

                writer.Write((short)0);
                writer.Write((short)1);
                writer.Write((short)1);

                writer.Write((byte)bitmap.Width);
                writer.Write((byte)bitmap.Height);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((short)1);
                writer.Write((short)32);
                writer.Write(pngBytes.Length);
                writer.Write(6 + 16);

                writer.Write(pngBytes);
                writer.Flush();
                icoStream.Seek(0, SeekOrigin.Begin);
                return new Icon(icoStream);
            }
        }
    }
}
