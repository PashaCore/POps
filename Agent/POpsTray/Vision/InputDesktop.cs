using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace POpsTray.Vision
{
    // Girdi masaüstü bu iş parçacığının masaüstü mü (karar: POps.Shared.VisionDesktop). Güvenli masaüstü (UAC onayı,
    // Ctrl+Alt+Del, kilit, oturum açma ekranı) etkinken kullanıcının tepsisi girdi masaüstünü açamaz; o sırada ne DXGI ne
    // GDI görüntü verir (DXGI erişimi kaybeder, GDI BitBlt hata döner). Yalnızca okuma izniyle açılır, hemen kapatılır;
    // masaüstüne geçilmez (SetThreadDesktop yok).
    [SupportedOSPlatform("windows")]
    internal static class InputDesktop
    {
        private const uint DesktopReadObjects = 0x0001;
        private const int UoiName = 2;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseDesktop(IntPtr desktop);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetThreadDesktop(uint threadId);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetUserObjectInformationW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] char[] info, int lengthInBytes, out int needed);

        // true: girdi masaüstü bu iş parçacığının masaüstü (yakalanabilir)
        public static bool IsOwn()
        {
            // GetThreadDesktop'un tanıtıcısı kapatılmaz (iş parçacığının kendi masaüstü)
            string? own = Name(GetThreadDesktop(GetCurrentThreadId()));
            IntPtr input = OpenInputDesktop(0, false, DesktopReadObjects);
            if (input == IntPtr.Zero) return POps.Shared.VisionDesktop.IsOwn(false, null, own);
            try { return POps.Shared.VisionDesktop.IsOwn(true, Name(input), own); }
            finally { _ = CloseDesktop(input); }
        }

        private static string? Name(IntPtr desktop)
        {
            if (desktop == IntPtr.Zero) return null;
            var buffer = new char[256];
            if (!GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length * sizeof(char), out _)) return null;
            int end = Array.IndexOf(buffer, '\0');
            return new string(buffer, 0, end < 0 ? buffer.Length : end);
        }
    }
}
