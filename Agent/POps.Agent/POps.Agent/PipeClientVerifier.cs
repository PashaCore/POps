using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

#nullable disable

namespace POpsAgent
{
    // Tepsi borusuna (POpsTrayPipe) bağlanan istemcinin gerçekten kurulu POpsTray.exe olduğunu doğrular.
    // Boru, oturum açmış her kullanıcıya açıktır; doğrulama olmadan bir öğrenci kendi yazdığı küçük bir istemciyle
    // bağlanıp yöneticiye sahte ekran görüntüsü gönderebilir, Vision tünelini isteyebilir ya da tepsinin yerine
    // geçebilirdi.
    //  * İstemci sürecinin görüntü yolu kurulum klasöründeki POpsTray.exe olmalı (Program Files'a yalnızca
    //    yöneticiler yazabilir; kopyası, sabit bağlantısı ya da başka bir exe kabul edilmez).
    //  * Süreç bir kullanıcı oturumunda (oturum 0 değil) çalışmalı.
    //  * Kurulu POpsTray.exe Authenticode imzalıysa imza geçerli olmalı. Bugünkü sürümler kod imzalı değil;
    //    imzalı bir sürüm kurulduğunda bu denetim kendiliğinden devreye girer.
    [SupportedOSPlatform("windows")]
    public static class PipeClientVerifier
    {
        // null: kabul; aksi halde ret nedeni.
        public static string Verify(SafePipeHandle pipe, string expectedImagePath) => Verify(pipe, expectedImagePath, out _);

        // clientProcessId: bağlanan tepsinin PID'i (yardım masası talebinin sahibi bu sürecin oturumundan belirlenir)
        public static string Verify(SafePipeHandle pipe, string expectedImagePath, out uint clientProcessId)
        {
            clientProcessId = 0;
            if (!GetNamedPipeClientProcessId(pipe, out uint pid))
                return $"istemci süreci belirlenemedi (hata {Marshal.GetLastWin32Error()})";
            clientProcessId = pid;

            string image = ProcessImagePath(pid);
            if (image == null) return $"istemci sürecinin (PID {pid}) yolu okunamadı";
            if (!SamePath(image, expectedImagePath)) return $"istemci {image} (PID {pid}); yalnızca {expectedImagePath} bağlanabilir";

            if (ProcessIdToSessionId(pid, out uint session) && session == 0)
                return $"istemci (PID {pid}) kullanıcı oturumunda değil";

            Authenticode.Result signature = Authenticode.VerifyCached(image);
            if (signature == Authenticode.Result.Invalid) return $"{image} imzası geçersiz";
            return null;
        }

        public static string ProcessImagePath(uint pid)
        {
            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, pid);
            if (process == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                int size = buffer.Capacity;
                return QueryFullProcessImageName(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
            }
            finally { CloseHandle(process); }
        }

        private static bool SamePath(string a, string b) =>
            string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);
    }

    // Gömülü Authenticode imzası (WinVerifyTrust). Kurum ağında sertifika iptal listesine erişim olmayabileceği
    // için iptal denetimi yapılmaz; zincir ve bütünlük denetlenir.
    [SupportedOSPlatform("windows")]
    public static class Authenticode
    {
        public enum Result { Unsigned, Valid, Invalid }

        private static readonly ConcurrentDictionary<string, (DateTime Stamp, Result Result)> Cache = new ConcurrentDictionary<string, (DateTime, Result)>(StringComparer.OrdinalIgnoreCase);

        public static Result VerifyCached(string path)
        {
            DateTime stamp = File.GetLastWriteTimeUtc(path);
            if (Cache.TryGetValue(path, out var cached) && cached.Stamp == stamp) return cached.Result;
            Result result = Verify(path);
            Cache[path] = (stamp, result);
            return result;
        }

        public static Result Verify(string path)
        {
            var file = new WintrustFileInfo { cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(), pcwszFilePath = path };
            IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            IntPtr dataPtr = IntPtr.Zero;
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new WintrustData
                {
                    cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                    dwUIChoice = WtdUiNone,
                    fdwRevocationChecks = WtdRevokeNone,
                    dwUnionChoice = WtdChoiceFile,
                    pFile = filePtr,
                    dwStateAction = WtdStateActionIgnore,
                    dwProvFlags = WtdCacheOnlyUrlRetrieval,
                };
                dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
                Marshal.StructureToPtr(data, dataPtr, false);

                uint status = unchecked((uint)WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr));
                if (status == 0) return Result.Valid;
                if (status == TrustENoSignature || status == TrustESubjectFormUnknown || status == TrustEProviderUnknown) return Result.Unsigned;
                return Result.Invalid;
            }
            finally
            {
                if (dataPtr != IntPtr.Zero) { Marshal.DestroyStructure<WintrustData>(dataPtr); Marshal.FreeHGlobal(dataPtr); }
                Marshal.DestroyStructure<WintrustFileInfo>(filePtr);
                Marshal.FreeHGlobal(filePtr);
            }
        }

        private static readonly Guid GenericVerifyV2 = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionIgnore = 0;
        private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
        private const uint TrustENoSignature = 0x800B0100;
        private const uint TrustESubjectFormUnknown = 0x800B0003;
        private const uint TrustEProviderUnknown = 0x800B0001;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WintrustFileInfo
        {
            public uint cbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
            public IntPtr hFile;
            public IntPtr pgKnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WintrustData
        {
            public uint cbStruct;
            public IntPtr pPolicyCallbackData;
            public IntPtr pSIPClientData;
            public uint dwUIChoice;
            public uint fdwRevocationChecks;
            public uint dwUnionChoice;
            public IntPtr pFile;
            public uint dwStateAction;
            public IntPtr hWVTStateData;
            public IntPtr pwszURLReference;
            public uint dwProvFlags;
            public uint dwUIContext;
            public IntPtr pSignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);
    }
}
