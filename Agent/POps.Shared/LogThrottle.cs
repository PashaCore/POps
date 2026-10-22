using System;
using System.Collections.Generic;

namespace POps.Shared
{
    // Döngüde tekrarlanan aynı hata logu doldurmasın: aynı anahtar (mesaj) en fazla "interval"de bir yazılır.
    public sealed class LogThrottle
    {
        private readonly TimeSpan _interval;
        private readonly Dictionary<string, DateTime> _last = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public LogThrottle(TimeSpan interval) => _interval = interval;

        // Dönen: bu mesaj şimdi yazılmalı mı (yazılacaksa zaman damgası güncellenir)
        public bool ShouldLog(string key, DateTime utcNow)
        {
            key ??= "";
            lock (_gate)
            {
                if (_last.TryGetValue(key, out DateTime last) && utcNow - last < _interval) return false;
                // Değişken metinli mesajlar sözlüğü büyütmesin
                if (_last.Count >= 256) _last.Clear();
                _last[key] = utcNow;
                return true;
            }
        }
    }
}
