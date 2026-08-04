using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace POpsVision
{
    public class ScreenEngine
    {
        private Device _device;
        private OutputDuplication _duplicatedOutput;
        private Texture2D _stagingTexture;

        public bool Start()
        {
            try
            {
                // 1. Ekran Kartı (GPU) ile tanışma
                var factory = new Factory1();
                var adapter = factory.GetAdapter1(0); // Birinci ekran kartı (UHD 770)
                _device = new Device(adapter);

                var output = adapter.GetOutput(0); // Birinci Monitör
                var output1 = output.QueryInterface<Output1>();

                // 2. İşletim sisteminin kalbine (Mirror Driver mantığı) kancayı at
                _duplicatedOutput = output1.DuplicateOutput(_device);

                // 3. Fotoğrafları koyacağımız RAM tepsisini (Staging Texture) hazırla
                var textureDesc = new Texture2DDescription
                {
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    Format = Format.B8G8R8A8_UNorm,
                    Width = output.Description.DesktopBounds.Right - output.Description.DesktopBounds.Left,
                    Height = output.Description.DesktopBounds.Bottom - output.Description.DesktopBounds.Top,
                    OptionFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Staging
                };
                _stagingTexture = new Texture2D(_device, textureDesc);

                Console.WriteLine($"[DXGI] Motor Çalıştı! Çözünürlük: {textureDesc.Width}x{textureDesc.Height}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DXGI HATA] Motor Başlatılamadı! {ex.Message}");
                return false;
            }
        }

        public byte[] GetNextFrameAsJpeg()
        {
            try
            {
                SharpDX.DXGI.Resource screenResource;
                OutputDuplicateFrameInformation frameInfo;

                // 🚀 ANYDESK SIRRI BURADA: 
                // Eğer ekranda hiçbir şey değişmediyse (fare bile oynamadıysa), ekran kartı bize fotoğraf vermez (Timeout olur).
                // Böylece ağı boş yere yormayız! (Delta Frame Mantığı)
                var result = _duplicatedOutput.TryAcquireNextFrame(50, out frameInfo, out screenResource);

                if (!result.Success) return null; // Ekran sabit, yeni fotoğrafa gerek yok.

                // Fotoğrafı ekran kartından al, bizim tepsiye koy
                using (var screenTexture2D = screenResource.QueryInterface<Texture2D>())
                {
                    _device.ImmediateContext.CopyResource(screenTexture2D, _stagingTexture);
                }

                screenResource.Dispose();
                _duplicatedOutput.ReleaseFrame();

                // Tepsiyi kilitle ve içindeki pikselleri JPEG'e çevir
                var mapSource = _device.ImmediateContext.MapSubresource(_stagingTexture, 0, MapMode.Read, MapFlags.None);

                using (var bitmap = new Bitmap(_stagingTexture.Description.Width, _stagingTexture.Description.Height, PixelFormat.Format32bppArgb))
                {
                    var boundsRect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
                    var mapDest = bitmap.LockBits(boundsRect, ImageLockMode.WriteOnly, bitmap.PixelFormat);

                    // Pikselleri kopyala (Satır satır)
                    var sourcePtr = mapSource.DataPointer;
                    var destPtr = mapDest.Scan0;
                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        Utilities.CopyMemory(destPtr, sourcePtr, bitmap.Width * 4);
                        sourcePtr = IntPtr.Add(sourcePtr, mapSource.RowPitch);
                        destPtr = IntPtr.Add(destPtr, mapDest.Stride);
                    }

                    bitmap.UnlockBits(mapDest);
                    _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);

                    // JPEG olarak sıkıştır (Kalite kaybı minimum, boyut muazzam düşük)
                    using (var ms = new MemoryStream())
                    {
                        bitmap.Save(ms, ImageFormat.Jpeg);
                        return ms.ToArray();
                    }
                }
            }
            catch
            {
                // Ekran çözünürlüğü değişirse veya kilitlenirse motoru koru
                return null;
            }
        }
    }
}