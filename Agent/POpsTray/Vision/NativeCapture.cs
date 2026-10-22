using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace POpsTray.Vision
{
    // Bir ekranın (ya da birleşik görüntünün) BGRA pikselleri
    internal sealed class ScreenImage
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int Stride => Width * 4;
        public byte[] Pixels { get; private set; } = Array.Empty<byte>();

        public void EnsureSize(int width, int height)
        {
            if (width == Width && height == Height) return;
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 4];
        }

        public void CopyFrom(ScreenImage other)
        {
            EnsureSize(other.Width, other.Height);
            Buffer.BlockCopy(other.Pixels, 0, Pixels, 0, Pixels.Length);
        }

        // Başka bir görüntüyü (x, 0) konumuna yerleştirir (yan yana birleşik görüntü)
        public void Blit(ScreenImage source, int x)
        {
            int rowBytes = Math.Min(source.Width, Width - x) * 4;
            for (int row = 0; row < Math.Min(source.Height, Height); row++)
                Buffer.BlockCopy(source.Pixels, row * source.Stride, Pixels, row * Stride + x * 4, rowBytes);
        }
    }

    internal sealed class MonitorInfo
    {
        public int Index { get; init; }
        public string DeviceName { get; init; } = "";
        public Rectangle Bounds { get; init; }   // fiziksel piksel
        public bool Primary { get; init; }
    }

    internal enum CaptureResult { NewFrame, NoChange, Lost }

    [SupportedOSPlatform("windows")]
    internal static class Dpi
    {
        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        // Bu iş parçacığı fiziksel piksellerle çalışır (yakalama ve imleç konumu); önceki bağlam döner
        public static IntPtr EnterPerMonitor() => SetThreadDpiAwarenessContext(PerMonitorAwareV2);

        public static void Restore(IntPtr previous)
        {
            if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous);
        }
    }

    // Ekranlar (fiziksel piksel; çağıran iş parçacığı per-monitor DPI bağlamında olmalı). Sıra: Windows'un sırası.
    [SupportedOSPlatform("windows")]
    internal static class Monitors
    {
        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct MONITORINFOEX
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);

        public static List<MonitorInfo> List()
        {
            var list = new List<MonitorInfo>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
                if (GetMonitorInfo(monitor, ref info) && list.Count < POps.Shared.VisionFrame.MaxMonitors)
                    list.Add(new MonitorInfo
                    {
                        Index = list.Count,
                        DeviceName = info.szDevice ?? "",
                        Bounds = Rectangle.FromLTRB(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom),
                        Primary = (info.dwFlags & 1) != 0,
                    });
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }

    // GDI yedeği: DXGI'nin olmadığı yerlerde (RDP, bazı sanal makineler)
    [SupportedOSPlatform("windows")]
    internal static class GdiCapture
    {
        public static void Capture(Rectangle bounds, ScreenImage target)
        {
            target.EnsureSize(bounds.Width, bounds.Height);
            using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
            using (Graphics g = Graphics.FromImage(bitmap))
                g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, bounds.Width, bounds.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < bounds.Height; row++)
                    Marshal.Copy(data.Scan0 + row * data.Stride, target.Pixels, row * target.Stride, target.Stride);
            }
            finally { bitmap.UnlockBits(data); }
        }
    }

    // DXGI Masaüstü Çoğaltma (IDXGIOutputDuplication). COM arayüzleri vtable sırasıyla çağrılır; sıralar ve IID'ler
    // Windows SDK'daki tanımlarla aynıdır (IDXGIFactory1::EnumAdapters1 12, IDXGIAdapter::EnumOutputs 7,
    // IDXGIOutput::GetDesc 7, IDXGIOutput1::DuplicateOutput 22, IDXGIOutputDuplication::AcquireNextFrame 8 /
    // ReleaseFrame 14, ID3D11Device::CreateTexture2D 5, ID3D11DeviceContext::Map 14 / Unmap 15 / CopyResource 47,
    // ID3D11Texture2D::GetDesc 10).
    [SupportedOSPlatform("windows")]
    internal sealed unsafe class DxgiDuplicator : IDisposable
    {
        private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
        private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
        private const int DxgiErrorWaitTimeout = unchecked((int)0x887A0027);

        private static readonly Guid IidFactory1 = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        private static readonly Guid IidOutput1 = new Guid("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IidTexture2D = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [StructLayout(LayoutKind.Sequential)]
        private struct OutputDesc
        {
            public fixed char DeviceName[32];
            public int Left, Top, Right, Bottom;
            public int AttachedToDesktop;
            public int Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FrameInfo
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX, PointerY, PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Texture2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedSubresource
        {
            public IntPtr Data;
            public uint RowPitch, DepthPitch;
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(Guid* riid, IntPtr* factory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
            uint levelCount, uint sdkVersion, IntPtr* device, int* featureLevel, IntPtr* context);

        private IntPtr _device, _context, _duplication, _staging;
        private uint _stagingWidth, _stagingHeight;

        private DxgiDuplicator() { }

        private static void* Slot(IntPtr obj, int index) => (*(void***)obj)[index];

        private static void Release(ref IntPtr obj)
        {
            if (obj == IntPtr.Zero) return;
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(obj, 2))(obj);
            obj = IntPtr.Zero;
        }

        private static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
        {
            IntPtr r;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(obj, 0))(obj, &iid, &r);
            result = r;
            return hr;
        }

        // Aygıt adına (\\.\DISPLAYn) göre çıktıyı bulur ve çoğaltmayı açar; olmazsa null (GDI'ye düşülür)
        public static DxgiDuplicator? TryCreate(string deviceName, out string? error)
        {
            error = null;
            IntPtr factory = IntPtr.Zero;
            Guid iid = IidFactory1;
            int hr = CreateDXGIFactory1(&iid, &factory);
            if (hr < 0) { error = $"CreateDXGIFactory1 0x{hr:X8}"; return null; }
            try
            {
                for (uint a = 0; ; a++)
                {
                    IntPtr adapter;
                    hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12))(factory, a, &adapter);
                    if (hr == DxgiErrorNotFound) break;
                    if (hr < 0) { error = $"EnumAdapters1 0x{hr:X8}"; return null; }
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr output;
                            hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, o, &output);
                            if (hr == DxgiErrorNotFound) break;
                            if (hr < 0) continue;
                            try
                            {
                                OutputDesc desc;
                                if (((delegate* unmanaged[Stdcall]<IntPtr, OutputDesc*, int>)Slot(output, 7))(output, &desc) < 0) continue;
                                string name = new string(desc.DeviceName);
                                int nul = name.IndexOf('\0', StringComparison.Ordinal);
                                if (nul >= 0) name = name.Substring(0, nul);
                                if (!string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase)) continue;
                                return Open(adapter, output, out error);
                            }
                            finally { Release(ref output); }
                        }
                    }
                    finally { Release(ref adapter); }
                }
                error = $"{deviceName} için DXGI çıktısı yok";
                return null;
            }
            finally { Release(ref factory); }
        }

        private static DxgiDuplicator? Open(IntPtr adapter, IntPtr output, out string? error)
        {
            error = null;
            var d = new DxgiDuplicator();
            IntPtr device, context, output1 = IntPtr.Zero;
            int level;
            // D3D_DRIVER_TYPE_UNKNOWN (adaptör verildiğinde), D3D11_CREATE_DEVICE_BGRA_SUPPORT, D3D11_SDK_VERSION 7
            int hr = D3D11CreateDevice(adapter, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, &device, &level, &context);
            if (hr < 0) { error = $"D3D11CreateDevice 0x{hr:X8}"; return null; }
            d._device = device;
            d._context = context;
            try
            {
                hr = QueryInterface(output, IidOutput1, out output1);
                if (hr < 0) { error = $"IDXGIOutput1 0x{hr:X8}"; d.Dispose(); return null; }
                IntPtr duplication;
                hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(output1, 22))(output1, device, &duplication);
                if (hr < 0) { error = $"DuplicateOutput 0x{hr:X8}"; d.Dispose(); return null; }
                d._duplication = duplication;
                return d;
            }
            finally { Release(ref output1); }
        }

        // timeoutMs içinde yeni kare yoksa NoChange. Lost: masaüstü değişti (UAC, kilit, çözünürlük); yeniden açılmalı.
        public CaptureResult TryCapture(ScreenImage target, int timeoutMs)
        {
            FrameInfo info;
            IntPtr resource;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, FrameInfo*, IntPtr*, int>)Slot(_duplication, 8))(_duplication, (uint)timeoutMs, &info, &resource);
            if (hr == DxgiErrorWaitTimeout) return CaptureResult.NoChange;
            if (hr < 0) return CaptureResult.Lost;
            IntPtr texture = IntPtr.Zero;
            try
            {
                // Yalnızca imleç değiştiyse görüntü yok
                if (info.LastPresentTime == 0 || info.AccumulatedFrames == 0) return CaptureResult.NoChange;
                if (QueryInterface(resource, IidTexture2D, out texture) < 0) return CaptureResult.Lost;
                Texture2DDesc desc;
                ((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, void>)Slot(texture, 10))(texture, &desc);
                if (!EnsureStaging(desc)) return CaptureResult.Lost;
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, void>)Slot(_context, 47))(_context, _staging, texture);
                MappedSubresource mapped;
                // D3D11_MAP_READ = 1
                if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, MappedSubresource*, int>)Slot(_context, 14))(_context, _staging, 0, 1, 0, &mapped) < 0)
                    return CaptureResult.Lost;
                try
                {
                    target.EnsureSize((int)desc.Width, (int)desc.Height);
                    fixed (byte* dst = target.Pixels)
                    {
                        for (int row = 0; row < target.Height; row++)
                            Buffer.MemoryCopy((byte*)mapped.Data + row * (long)mapped.RowPitch, dst + row * (long)target.Stride, target.Stride, target.Stride);
                    }
                }
                finally { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, void>)Slot(_context, 15))(_context, _staging, 0); }
                return CaptureResult.NewFrame;
            }
            finally
            {
                Release(ref texture);
                Release(ref resource);
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(_duplication, 14))(_duplication);
            }
        }

        private bool EnsureStaging(Texture2DDesc source)
        {
            if (_staging != IntPtr.Zero && _stagingWidth == source.Width && _stagingHeight == source.Height) return true;
            Release(ref _staging);
            // D3D11_USAGE_STAGING 3, D3D11_CPU_ACCESS_READ 0x20000
            var desc = new Texture2DDesc
            {
                Width = source.Width, Height = source.Height, MipLevels = 1, ArraySize = 1, Format = source.Format,
                SampleCount = 1, SampleQuality = 0, Usage = 3, BindFlags = 0, CpuAccessFlags = 0x20000, MiscFlags = 0,
            };
            IntPtr staging;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Texture2DDesc*, IntPtr, IntPtr*, int>)Slot(_device, 5))(_device, &desc, IntPtr.Zero, &staging) < 0) return false;
            _staging = staging;
            _stagingWidth = source.Width;
            _stagingHeight = source.Height;
            return true;
        }

        public void Dispose()
        {
            Release(ref _staging);
            Release(ref _duplication);
            Release(ref _context);
            Release(ref _device);
        }
    }
}
