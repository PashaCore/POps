using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

#nullable disable

namespace POpsAgent
{
    // Politika yanıtıyla gelen değişiklik. First: servis açıldıktan sonraki ilk yanıt (önceki durum bilinmiyordu).
    public sealed class ModuleChange
    {
        public IReadOnlyList<string> Closed { get; init; } = Array.Empty<string>();
        public IReadOnlyList<string> Opened { get; init; } = Array.Empty<string>();
        public bool First { get; init; }
        public bool Any => Closed.Count > 0 || Opened.Count > 0;
    }

    // Sunucunun laboratuvar bazında açıp kapattığı modüller (0.1.15 sunucu). Ajan politika isteğine anahtarını
    // (X-Agent-Id + X-Agent-Secret) ekler; sunucu yanıtta cihazın laboratuvarı için "modules" döner
    // ({modül: bool}). Alan yoksa (eski sunucu, anahtarsız cihaz) her modül açık sayılır. Politika dakikada bir
    // yenilenir; istek başarısızsa son bilinen durum kalır.
    // Durum yalnızca bellekte tutulur, capabilities.json'a hiç yazılmaz: yerel yetenek kilidi ayrı kalır ve sunucu
    // modülü yeniden açınca özellik kendiliğinden geri gelir. Etkin = sunucuda modül açık VE yerelde yetenek izinli.
    // Bu çalışmanın durumu bağlamdaki ModuleState örneğindedir (bkz. AgentContext.Modules); burada modül adları kalır.
    public static class AgentModules
    {
        public const string Vision = "vision", Terminal = "terminal", Deploy = "deploy", Schedules = "schedules",
            Patches = "patches", Software = "software", Licenses = "licenses", Helpdesk = "helpdesk",
            DnsPolicy = "dns_policy", Quarantine = "quarantine", Wol = "wol", Reports = "reports";

        // capability_denied "reason": istek, modül kapalı olduğu için reddedildi
        public const string DisabledReason = "module_disabled";
    }

    // Bir ajanın sunucu modülleri (bkz. AgentModules): yalnızca bellekte, ilk politika yanıtına kadar hepsi açık. Servis için
    // Program bir kez kurar, her test kendi örneğini kurar.
    public sealed class ModuleState
    {
        private readonly object _sync = new object();
        // Kapalı modüller; boş: hepsi açık
        private HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);
        private bool _known;

        public bool IsEnabled(string module)
        {
            lock (_sync) return !_closed.Contains(module);
        }

        public IReadOnlyList<string> Closed()
        {
            lock (_sync) return _closed.OrderBy(m => m, StringComparer.Ordinal).ToList();
        }

        // Politika yanıtındaki "modules": yalnızca false olanlar kapalıdır; alan yoksa ya da nesne değilse hepsi açık.
        public ModuleChange Apply(JsonElement policy)
        {
            var closed = new HashSet<string>(StringComparer.Ordinal);
            if (policy.ValueKind == JsonValueKind.Object && policy.TryGetProperty("modules", out JsonElement modules)
                && modules.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty module in modules.EnumerateObject())
                    if (module.Value.ValueKind == JsonValueKind.False) closed.Add(module.Name);
            }

            lock (_sync)
            {
                var change = new ModuleChange
                {
                    Closed = closed.Where(m => !_closed.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList(),
                    Opened = _closed.Where(m => !closed.Contains(m)).OrderBy(m => m, StringComparer.Ordinal).ToList(),
                    First = !_known,
                };
                _closed = closed;
                _known = true;
                return change;
            }
        }
    }
}
