using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

#nullable disable

namespace POpsAgent
{
    // DNS tabanlı içerik tespiti.
    //  * Kaynak: Windows DNS istemci önbelleği (dnsapi DnsGetCacheDataTable). Eskiden "ipconfig /displaydns"
    //    çıktısındaki İngilizce "Record Name" etiketi aranıyordu; Türkçe Windows'ta etiket farklı olduğu için
    //    tespit hiç çalışmıyordu.
    //  * Eşleşme: yalnızca okulun politikasında (dns_domains) verdiği alan adları ve onların alt alanları.
    //    Eskiden alan adının İÇİNDE "sex", "bet" gibi parçalar aranıyordu: "essex.ac.uk" ya da "alphabet.com"
    //    gibi masum adresler reşit olmayan bir öğrencinin kaydına "pornografi"/"bahis" olarak düşüyordu (KVKK).
    //    Liste yoksa tespit yapılmaz.
    // Politikadaki alan adı listelerinin normalleştirilmiş dizini: politika değişince BİR KEZ kurulur. Önbellekteki her
    // ad kendi sonek zinciriyle aranır (a.b.c -> a.b.c, b.c, c): liste büyüklüğünden bağımsız, ad başına birkaç sözlük
    // araması. Bir ad birden çok kategoriye uyarsa politikadaki kategori sırası belirler.
    public sealed class DnsDomainIndex
    {
        private readonly Dictionary<string, (string Category, int Order)> _entries = new Dictionary<string, (string, int)>(StringComparer.Ordinal);

        public static readonly DnsDomainIndex Empty = new DnsDomainIndex(null, null);

        public DnsDomainIndex(IEnumerable<string> activeCategories, IDictionary<string, List<string>> domainsByCategory)
        {
            if (activeCategories == null || domainsByCategory == null) return;
            int order = 0;
            foreach (string category in activeCategories)
            {
                order++;
                if (category == null || !domainsByCategory.TryGetValue(category, out List<string> listed) || listed == null) continue;
                foreach (string entry in listed)
                {
                    string name = DnsWatch.Normalize(entry);
                    if (name != null && !_entries.ContainsKey(name)) _entries[name] = (category, order);
                }
            }
        }

        public int Count => _entries.Count;

        public (string Category, string Entry)? Match(string domain)
        {
            if (_entries.Count == 0) return null;
            string name = DnsWatch.Normalize(domain);
            if (name == null) return null;
            (string Category, string Entry)? best = null;
            int bestOrder = int.MaxValue;
            string suffix = name;
            while (true)
            {
                if (_entries.TryGetValue(suffix, out var hit) && hit.Order < bestOrder)
                {
                    best = (hit.Category, suffix);
                    bestOrder = hit.Order;
                }
                int dot = suffix.IndexOf('.');
                if (dot < 0 || dot == suffix.Length - 1) break;
                suffix = suffix.Substring(dot + 1);
            }
            return best;
        }
    }

    [SupportedOSPlatform("windows")]
    public static class DnsWatch
    {
        // "sub.example.com" -> "example.com" listede ise eşleşir; "badexample.com" eşleşmez.
        public static string MatchCategory(string domain, IEnumerable<string> activeCategories, IDictionary<string, List<string>> domainsByCategory) =>
            Match(domain, activeCategories, domainsByCategory)?.Category;

        // Eşleşen kategori ve listedeki giriş (normalleştirilmiş). www., cdn., static. gibi alt alanlar aynı girişe düşer.
        // Tek seferlik eşleştirme içindir; sürekli izlemede dizin bir kez kurulur (bkz. DnsDomainIndex).
        public static (string Category, string Entry)? Match(string domain, IEnumerable<string> activeCategories, IDictionary<string, List<string>> domainsByCategory) =>
            new DnsDomainIndex(activeCategories, domainsByCategory).Match(domain);

        // Küçük harf, sondaki nokta atılır; Türkçe karakterli (IDN) adlar punycode'a çevrilir: DNS önbelleğinde
        // "xn--..." biçimi durur, okul listeye "örnek.com" yazmış olabilir.
        public static string Normalize(string domain)
        {
            string name = domain?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(name) || name.Contains(' ')) return null;
            // DNS önbelleğindeki adlar zaten ASCII'dir (punycode); IDN dönüşümü (ICU) yalnızca ASCII dışı adlar için.
            // IdnMapping örnekleri thread'ler arasında paylaşılmaz.
            if (!IsAscii(name))
            {
                try { name = new IdnMapping().GetAscii(name); }
                catch (ArgumentException) { }
            }
            return name.ToLowerInvariant();
        }

        private static bool IsAscii(string value)
        {
            foreach (char c in value)
                if (c > 0x7F) return false;
            return true;
        }

        // DNS istemci önbelleğindeki adlar (yinelenmeden). Okunamazsa boş liste.
        public static List<string> ReadCacheNames()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            if (!DnsGetCacheDataTable(out IntPtr entry)) return names.ToList();
            while (entry != IntPtr.Zero)
            {
                DnsCacheEntry e = Marshal.PtrToStructure<DnsCacheEntry>(entry);
                string name = Normalize(Marshal.PtrToStringUni(e.Name));
                if (name != null) names.Add(name);
                IntPtr next = e.Next;
                DnsFree(e.Name, DnsFreeFlat);
                DnsFree(entry, DnsFreeFlat);
                entry = next;
            }
            return names.ToList();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DnsCacheEntry
        {
            public IntPtr Next;
            public IntPtr Name;
            public ushort Type;
            public ushort DataLength;
            public uint Flags;
        }

        private const int DnsFreeFlat = 0;

        [DllImport("dnsapi.dll", EntryPoint = "DnsGetCacheDataTable")]
        private static extern bool DnsGetCacheDataTable(out IntPtr table);

        [DllImport("dnsapi.dll", EntryPoint = "DnsFree")]
        private static extern void DnsFree(IntPtr data, int freeType);
    }
}
