using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    // Dosya aktarımı (yetenek files_enabled; yerelde kapatılabilir, sunucu yalnızca kapatabilir).
    //  * file_push (yönetici -> PC): yalnızca ajanın kendi sunucusundan indirilir, boyut ve SHA-256 doğrulanır;
    //    yalnızca izinli yerlere yazılır: public_desktop (C:\Users\Public\Desktop) ya da inbox
    //    (C:\POpsData\inbox\<tarih>, Users okur, SYSTEM/Administrators tam). Ad temizlenir (yol ayırıcı, ADS, ayrılmış
    //    ad yok), çakışırsa " (2)" eklenir. .lnk, .url ve .scr yalnızca allow_exec ile.
    //  * file_pull (PC -> yönetici; gerekçe zorunlu): C:\POpsData\secure içinden hiçbir şey, başka kullanıcıların
    //    profilinden yalnızca any_profile ile, max_size'dan büyük dosya asla. Denetim dosyanın gerçek yoluna
    //    (bağlantılar çözülmüş) yapılır; cihaz anahtarı başlıklarıyla yüklenir.
    // Her işlem PC kullanıcısına tepsiden söylenir ve yerel denetim kaydına yazılır.
    [SupportedOSPlatform("windows")]
    public static class FileTransfer
    {
        public const long MaxBytes = 1024L * 1024 * 1024;
        // Sunucu şeması: name en çok 200, reason 3-300, path en çok 1024 karakter
        public const int MaxNameLength = 200, MinReason = 3, MaxReason = 300, MaxPathLength = 1024;
        private static readonly string[] ExecLike = { ".lnk", ".url", ".scr" };
        private static readonly Regex Reserved = new Regex(@"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9]|CONIN\$|CONOUT\$)(\..*)?$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        // Sunucunun aktarım kimliği (rastgele, URL'de kullanılabilir)
        private static readonly Regex TransferIdRegex = new Regex(@"^[A-Za-z0-9_-]{8,64}\z", RegexOptions.Compiled);
        private static readonly Regex Sha256Regex = new Regex(@"^[0-9a-f]{64}\z", RegexOptions.Compiled);
        // url / upload: ajanın kendi sunucusunda göreli yol, tek kullanımlık jetonla (sunucu şemasıyla aynı)
        private static readonly Regex DownloadPath = new Regex(@"^/api/files/[A-Za-z0-9_-]{8,64}/download\?t=[A-Za-z0-9_-]+\z", RegexOptions.Compiled);
        private static readonly Regex UploadPath = new Regex(@"^/api/files/[A-Za-z0-9_-]{8,64}/upload\?t=[A-Za-z0-9_-]+\z", RegexOptions.Compiled);
        private static readonly Regex DrivePath = new Regex(@"^[A-Za-z]:\\", RegexOptions.Compiled);
        private static readonly char[] Wildcards = { '*', '?', '"', '<', '>', '|' };

        public sealed class PushRequest
        {
            public string TransferId { get; init; }
            public string Name { get; init; }
            public long Size { get; init; }
            public string Sha256 { get; init; }
            public Uri Url { get; init; }
            public string Dest { get; init; }
            public string Reason { get; init; }
            public bool AllowExec { get; init; }
        }

        public sealed class PullRequest
        {
            public string TransferId { get; init; }
            public string Path { get; init; }
            public long MaxSize { get; init; }
            public Uri Upload { get; init; }
            public string Reason { get; init; }
            public bool AnyProfile { get; init; }
        }

        // Testler: yollar ve kullanıcının profili
        internal static Func<string> PublicDesktopOverride { get; set; }
        internal static Func<(string ProfilesDirectory, string CurrentProfile)> ProfileInfo { get; set; } = ReadProfiles;

        public static string PublicDesktop => PublicDesktopOverride?.Invoke() ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        public static string InboxRoot => System.IO.Path.Combine(AgentUpdate.DataDir, "inbox");

        // Geçersiz ya da eksik kimlik: null (sunucu bilmediği aktarımın sonucunu yok sayar; böyle emre file_result gitmez)
        public static string TransferIdOf(JsonElement command) =>
            Text(command, "transfer_id") is string id && TransferIdRegex.IsMatch(id) ? id : null;

        // ------------------------------------------------------------------ doğrulama
        public static bool TryParsePush(JsonElement c, string serverUrl, out PushRequest request, out string error)
        {
            request = null;
            string id = TransferIdOf(c);
            if (id == null) { error = "transfer_id geçersiz"; return false; }
            string name = SanitizeName(Text(c, "name"));
            if (name == null) { error = "dosya adı geçersiz"; return false; }
            long size = c.TryGetProperty("size", out JsonElement s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out long v) ? v : -1;
            if (size < 0 || size > MaxBytes) { error = $"boyut geçersiz (en çok {MaxBytes / (1024 * 1024)} MB)"; return false; }
            string sha = Text(c, "sha256")?.ToLowerInvariant();
            if (sha == null || !Sha256Regex.IsMatch(sha)) { error = "sha256 geçersiz"; return false; }
            Uri url = ServerUri(serverUrl, Text(c, "url"), upload: false);
            if (url == null) { error = "indirme adresi ajanın sunucusunda değil"; return false; }
            string dest = Text(c, "dest");
            if (dest != "public_desktop" && dest != "inbox") { error = "hedef yalnızca public_desktop ya da inbox olabilir"; return false; }
            if (!TryReason(c, out string reason, out error)) return false;
            bool allowExec = c.TryGetProperty("allow_exec", out JsonElement ae) && ae.ValueKind == JsonValueKind.True;
            if (!allowExec && ExecLike.Contains(System.IO.Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
            {
                error = $"{System.IO.Path.GetExtension(name)} dosyası yalnızca allow_exec ile gönderilebilir";
                return false;
            }
            request = new PushRequest { TransferId = id, Name = name, Size = size, Sha256 = sha, Url = url, Dest = dest, Reason = reason, AllowExec = allowExec };
            error = null;
            return true;
        }

        public static bool TryParsePull(JsonElement c, string serverUrl, out PullRequest request, out string error)
        {
            request = null;
            string id = TransferIdOf(c);
            if (id == null) { error = "transfer_id geçersiz"; return false; }
            // Sürücü harfli tam yerel yol (ağ ve aygıt yolu yok); joker ve ':' (ADS) yalnızca sürücü harfinden sonra yok
            string path = Text(c, "path");
            if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength || !DrivePath.IsMatch(path) || !System.IO.Path.IsPathFullyQualified(path)
                || path.IndexOf(':', 2) >= 0 || path.IndexOfAny(Wildcards) >= 0)
            {
                error = "yol yerel ve tam olmalı";
                return false;
            }
            long max = c.TryGetProperty("max_size", out JsonElement m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out long v) ? v : -1;
            if (max <= 0 || max > MaxBytes) { error = "max_size geçersiz"; return false; }
            Uri upload = ServerUri(serverUrl, Text(c, "upload"), upload: true);
            if (upload == null) { error = "yükleme adresi ajanın sunucusunda değil"; return false; }
            if (!TryReason(c, out string reason, out error)) return false;
            bool any = c.TryGetProperty("any_profile", out JsonElement ap) && ap.ValueKind == JsonValueKind.True;
            request = new PullRequest { TransferId = id, Path = path, MaxSize = max, Upload = upload, Reason = reason, AnyProfile = any };
            error = null;
            return true;
        }

        // Gerekçe zorunlu (sunucu şeması: 3-300 karakter); denetim karakterleri atılır
        private static bool TryReason(JsonElement c, out string reason, out string error)
        {
            string text = Text(c, "reason")?.Trim();
            if (text == null || text.Length < MinReason)
            {
                reason = null;
                error = $"gerekçe zorunlu (en az {MinReason} karakter)";
                return false;
            }
            reason = LogText.Safe(text, MaxReason);
            error = null;
            return true;
        }

        // Yalnızca sunucunun verdiği biçim: "/api/files/<kimlik>/download?t=<jeton>" (upload: ".../upload?t=..."), ajanın
        // kendi sunucusuna göre (ServerUrl + yol, öteki istekler gibi; sorgu dizisi korunur). Başka her şey null.
        internal static Uri ServerUri(string serverUrl, string value, bool upload)
        {
            if (string.IsNullOrEmpty(value) || !(upload ? UploadPath : DownloadPath).IsMatch(value) || !POpsHelpers.IsSecureServerUrl(serverUrl)) return null;
            return Uri.TryCreate(serverUrl.TrimEnd('/') + value, UriKind.Absolute, out Uri target) ? target : null;
        }

        // Yol ayırıcısı, ':' (ADS), joker ve denetim karakteri yok; ayrılmış ad yok; sondaki nokta/boşluk atılır
        public static string SanitizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            string trimmed = name.Trim().TrimEnd('.', ' ');
            if (trimmed.Length == 0 || trimmed.Length > MaxNameLength || trimmed == "." || trimmed == "..") return null;
            if (trimmed.Any(ch => ch < 32 || "\\/:*?\"<>|".Contains(ch, StringComparison.Ordinal))) return null;
            if (Reserved.IsMatch(trimmed)) return null;
            return trimmed;
        }

        // Ad çakışırsa "ad (2).uzantı", "ad (3).uzantı" ...
        public static string UniquePath(string directory, string name)
        {
            string path = System.IO.Path.Combine(directory, name);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string stem = System.IO.Path.GetFileNameWithoutExtension(name), ext = System.IO.Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                path = System.IO.Path.Combine(directory, $"{stem} ({i}){ext}");
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
        }

        // Çekilecek dosyanın gerçek yolu: secure klasörü asla; başka kullanıcının profili yalnızca anyProfile ile
        public static string CheckPullPath(string finalPath, string secureDir, string profilesDirectory, string currentProfile, bool anyProfile)
        {
            if (Under(finalPath, secureDir)) return "POps güvenli klasöründen dosya alınamaz";
            if (!anyProfile && Under(finalPath, profilesDirectory))
            {
                bool own = currentProfile != null && Under(finalPath, currentProfile);
                bool shared = Under(finalPath, System.IO.Path.Combine(profilesDirectory, "Public"));
                if (!own && !shared) return "başka bir kullanıcının profili (any_profile gerekir)";
            }
            return null;
        }

        private static bool Under(string path, string directory)
        {
            if (string.IsNullOrEmpty(directory)) return false;
            string dir = System.IO.Path.GetFullPath(directory).TrimEnd('\\') + "\\";
            return System.IO.Path.GetFullPath(path).StartsWith(dir, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ işlemler
        // Sonuç "outcome" alanındadır, "status" değil: eski sunucu tanımadığı ve status taşıyan her mesajı heartbeat sayar.
        // Sunucu şeması: path en çok 1024 karakter (daha uzun yol gönderilmez), detail en çok 500 (burada 300).
        public static Dictionary<string, object> Result(string transferId, string outcome, string path = null, string detail = null)
        {
            var message = new Dictionary<string, object> { ["type"] = "file_result", ["transfer_id"] = transferId, ["outcome"] = outcome };
            if (path != null && path.Length <= MaxPathLength) message["path"] = path;
            if (detail != null) message["detail"] = LogText.Safe(detail, MaxReason);
            return message;
        }

        // Dönen: (sonuç, yol, açıklama); sonuç "done" | "rejected" | "failed"
        public static async Task<(string Outcome, string Path, string Detail)> PushAsync(PushRequest request, string hwId, DateTime localNow, CancellationToken token)
        {
            string destination = request.Dest == "public_desktop" ? PublicDesktop : InboxFor(localNow);
            if (string.IsNullOrEmpty(destination) || !Directory.Exists(destination)) return ("failed", null, "hedef klasör yok");
            string partial = SecureStore.PathOf($"transfer-{request.TransferId}.part");
            try
            {
                SecureStore.EnsureDirectory();
                using var message = new HttpRequestMessage(HttpMethod.Get, request.Url);
                AgentCredentials.AddHttpAuth(message, hwId);
                using HttpResponseMessage response = await AgentHttp.Client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);
                if (!response.IsSuccessStatusCode) return ("failed", null, $"indirilemedi: HTTP {(int)response.StatusCode}");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long total = 0;
                await using (Stream input = await response.Content.ReadAsStreamAsync(token))
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await input.ReadAsync(buffer.AsMemory(), token)) > 0)
                    {
                        total += read;
                        if (total > request.Size) return ("failed", null, "dosya bildirilen boyuttan büyük");
                        hash.AppendData(buffer, 0, read);
                        await output.WriteAsync(buffer.AsMemory(0, read), token);
                    }
                }
                string actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (total != request.Size || actual != request.Sha256) return ("failed", null, $"boyut ya da SHA-256 uyuşmuyor ({total}/{request.Size})");
                // Kopyalanır (taşınmaz): dosya hedef klasörün izinlerini alsın (güvenli klasörün kilidini değil)
                string target = UniquePath(destination, request.Name);
                File.Copy(partial, target, false);
                return ("done", target, null);
            }
            catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is UnauthorizedAccessException || ex is TaskCanceledException)
            {
                return ("failed", null, ex.Message);
            }
            finally { TryDelete(partial); }
        }

        public static async Task<(string Outcome, string Path, string Detail, long Size)> PullAsync(PullRequest request, string hwId, CancellationToken token)
        {
            FileStream file;
            try { file = new FileStream(request.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { return ("failed", request.Path, ex.Message, 0); }
            await using (file)
            {
                string finalPath = FinalPath(file.SafeFileHandle) ?? System.IO.Path.GetFullPath(request.Path);
                var (profiles, current) = ProfileInfo();
                string refusal = CheckPullPath(finalPath, SecureStore.Dir, profiles, current, request.AnyProfile);
                if (refusal != null) return ("rejected", finalPath, refusal, 0);
                long size = file.Length;
                if (size > request.MaxSize) return ("rejected", finalPath, $"dosya max_size'dan büyük ({size} bayt)", size);
                try
                {
                    using var message = new HttpRequestMessage(HttpMethod.Post, request.Upload) { Content = new StreamContent(file, 81920) };
                    message.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
                    message.Content.Headers.ContentLength = size;
                    AgentCredentials.AddHttpAuth(message, hwId);
                    using HttpResponseMessage response = await AgentHttp.Client.SendAsync(message, token);
                    return response.IsSuccessStatusCode
                        ? ("done", finalPath, null, size)
                        : ("failed", finalPath, $"yüklenemedi: HTTP {(int)response.StatusCode}", size);
                }
                catch (Exception ex) when (ex is IOException || ex is HttpRequestException || ex is TaskCanceledException)
                {
                    return ("failed", finalPath, ex.Message, size);
                }
            }
        }

        // C:\POpsData\inbox\<yyyy-MM-dd>: Users okur, SYSTEM/Administrators tam (devralma yok)
        internal static string InboxFor(DateTime localNow)
        {
            string dir = System.IO.Path.Combine(InboxRoot, localNow.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(true, false);
            sec.AddAccessRule(new FileSystemAccessRule(SecureStore.SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            var info = new DirectoryInfo(dir);
            if (info.Exists) info.SetAccessControl(sec);
            else info.Create(sec);
            return dir;
        }

        private static string Text(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        // ------------------------------------------------------------------ yerel sistem
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, char[] path, uint size, uint flags);

        [DllImport("wtsapi32.dll", SetLastError = true)]
        private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        // Bağlantılar (symlink, junction) çözülmüş gerçek yol; "\\?\" öneki atılır
        internal static string FinalPath(SafeFileHandle handle)
        {
            char[] buffer = new char[1024];
            uint length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0 || length >= buffer.Length) return null;
            string path = new string(buffer, 0, (int)length);
            if (path.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return string.Concat(@"\\", path.AsSpan(8));
            return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        }

        // Profil kökü (ProfileList\ProfilesDirectory) ve konsoldaki kullanıcının profili (yoksa null)
        private static (string, string) ReadProfiles()
        {
            using RegistryKey list = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            string profiles = Environment.ExpandEnvironmentVariables(list?.GetValue("ProfilesDirectory") as string ?? @"%SystemDrive%\Users");
            string current = null;
            if (WTSQueryUserToken(UserSessionLauncher.ActiveConsoleSession(), out IntPtr token))
            {
                try
                {
                    using var identity = new WindowsIdentity(token);
                    string sid = identity.User?.Value;
                    using RegistryKey profile = sid == null ? null : list?.OpenSubKey(sid);
                    if (profile?.GetValue("ProfileImagePath") is string image) current = Environment.ExpandEnvironmentVariables(image);
                }
                finally { CloseHandle(token); }
            }
            return (profiles, current);
        }
    }
}
