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
    [SupportedOSPlatform("windows")]
    public static class DnsWatch
    {
        // "sub.example.com" -> "example.com" listede ise eşleşir; "badexample.com" eşleşmez.
        public static string MatchCategory(string domain, IEnumerable<string> activeCategories, IDictionary<string, List<string>> domainsByCategory) =>
            Match(domain, activeCategories, domainsByCategory)?.Category;

        // Eşleşen kategori ve listedeki giriş (normalleştirilmiş). www., cdn., static. gibi alt alanlar aynı girişe düşer.
        public static (string Category, string Entry)? Match(string domain, IEnumerable<string> activeCategories, IDictionary<string, List<string>> domainsByCategory)
        {
            string name = Normalize(domain);
            if (name == null || domainsByCategory == null || activeCategories == null) return null;

            foreach (string category in activeCategories)
            {
                if (category == null || !domainsByCategory.TryGetValue(category, out List<string> listed) || listed == null) continue;
                foreach (string entry in listed)
                {
                    string listedName = Normalize(entry);
                    if (listedName == null) continue;
                    if (name == listedName || name.EndsWith("." + listedName, StringComparison.Ordinal)) return (category, listedName);
                }
            }
            return null;
        }

        // Küçük harf, sondaki nokta atılır; Türkçe karakterli (IDN) adlar punycode'a çevrilir: DNS önbelleğinde
        // "xn--..." biçimi durur, okul listeye "örnek.com" yazmış olabilir.
        public static string Normalize(string domain)
        {
            string name = domain?.Trim().TrimEnd('.');
            if (string.IsNullOrEmpty(name) || name.Contains(' ')) return null;
            // IdnMapping örnekleri thread'ler arasında paylaşılmaz
            try { name = new IdnMapping().GetAscii(name); }
            catch (ArgumentException) { }
            return name.ToLowerInvariant();
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
