using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace POpsTray.Vision
{
    // Kullanıcının masaüstü görünmüyorken (güvenli masaüstü: UAC onayı, Ctrl+Alt+Del, kilit, oturum açma ekranı)
    // görüntüleyiciye donmuş son kare yerine giden bildirim resmi. Ekrandan bir şey okunmaz, yalnızca sabit metin
    // çizilir. Her zamanki kare biçimiyle gider (eski yol: JPEG stream_frame; v2: tam kare), protokol değişmez.
    [SupportedOSPlatform("windows")]
    internal static class SecureDesktopNotice
    {
        private const string Title = "Güvenli masaüstü etkin";
        private const string Body = "UAC onayı, Ctrl+Alt+Del, kilit ya da oturum açma ekranı açık. POps bu ekranı göstermez ve ona tıklayamaz.\n"
            + "Görüntü, kullanıcının masaüstü geri gelince kendiliğinden sürer.";
        private const string English = "Secure desktop active (UAC prompt, Ctrl+Alt+Del, lock or sign-in screen). POps does not show or "
            + "control it; the picture resumes when the user's desktop returns.";

        private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");

        public static Bitmap Render(int width, int height)
        {
            width = Math.Max(1, width);
            height = Math.Max(1, height);
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
            try
            {
                using Graphics g = Graphics.FromImage(bitmap);
                g.Clear(Color.FromArgb(32, 33, 36));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                float titleSize = Math.Clamp(height / 18f, 12f, 72f);
                float bodySize = Math.Clamp(height / 42f, 9f, 30f);
                using var titleFont = new Font("Segoe UI", titleSize, FontStyle.Bold, GraphicsUnit.Pixel);
                using var bodyFont = new Font("Segoe UI", bodySize, FontStyle.Regular, GraphicsUnit.Pixel);
                using var englishFont = new Font("Segoe UI", bodySize * 0.85f, FontStyle.Regular, GraphicsUnit.Pixel);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Near };
                using var white = new SolidBrush(Color.FromArgb(240, 240, 240));
                using var grey = new SolidBrush(Color.FromArgb(160, 164, 170));

                float margin = width * 0.08f;
                float layoutWidth = Math.Max(1f, width - 2 * margin);
                float gap = bodySize;
                SizeF title = g.MeasureString(Title, titleFont, (int)layoutWidth, format);
                SizeF body = g.MeasureString(Body, bodyFont, (int)layoutWidth, format);
                SizeF english = g.MeasureString(English, englishFont, (int)layoutWidth, format);
                float y = Math.Max(0f, (height - (title.Height + gap + body.Height + gap * 2 + english.Height)) / 2);

                g.DrawString(Title, titleFont, white, new RectangleF(margin, y, layoutWidth, title.Height), format);
                y += title.Height + gap;
                g.DrawString(Body, bodyFont, white, new RectangleF(margin, y, layoutWidth, body.Height), format);
                y += body.Height + gap * 2;
                g.DrawString(English, englishFont, grey, new RectangleF(margin, y, layoutWidth, english.Height), format);
                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        // Vision v2: BGRA piksellere (kare her zamanki gibi kodlanır ve boyut sınırı denetlenir)
        public static void RenderInto(ScreenImage target, int width, int height)
        {
            using Bitmap bitmap = Render(width, height);
            target.EnsureSize(bitmap.Width, bitmap.Height);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < bitmap.Height; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, target.Pixels, row * target.Stride, target.Stride);
            }
            finally { bitmap.UnlockBits(data); }
        }

        // Eski yol (JSON stream_frame): JPEG; kodlanamazsa null
        public static byte[]? Jpeg(int width, int height, long quality)
        {
            if (JpegCodec == null) return null;
            try
            {
                using Bitmap bitmap = Render(width, height);
                using var parameters = new EncoderParameters(1);
                parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                using var output = new MemoryStream();
                bitmap.Save(output, JpegCodec, parameters);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is ExternalException || ex is ArgumentException) { return null; }
        }
    }
}
