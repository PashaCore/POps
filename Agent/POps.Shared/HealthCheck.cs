using System;
using System.Text.Json;

namespace POps.Shared
{
    public enum StartupCheck
    {
        Identity,
        Credentials,
        Capabilities,
        Pipe,
        Loop,
    }

    public sealed class OperationalChecks
    {
        public bool Identity { get; init; }
        public bool Credentials { get; init; }
        public bool Capabilities { get; init; }
        public bool Pipe { get; init; }
        public bool Loop { get; init; }

        public bool Complete => Identity && Credentials && Capabilities && Pipe && Loop;
    }

    // Worker'ın çekirdek açılış adımları tamamlanmadan health.json yazılmasını engeller.
    public sealed class OperationalHealthGate
    {
        private readonly object _gate = new object();
        private readonly bool _suppressed;
        private readonly Action<OperationalChecks> _write;
        private bool _identity, _credentials, _capabilities, _pipe, _loop, _written;

        public OperationalHealthGate(bool suppressed, Action<OperationalChecks> write)
        {
            _suppressed = suppressed;
            _write = write ?? throw new ArgumentNullException(nameof(write));
        }

        // Eylem istisna atarsa ilgili kontrol tamamlanmış sayılmaz ve sağlık yazılmaz.
        public void Run(StartupCheck check, Action action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            action();
            Mark(check);
        }

        public void Mark(StartupCheck check)
        {
            OperationalChecks? snapshot = null;
            lock (_gate)
            {
                switch (check)
                {
                    case StartupCheck.Identity: _identity = true; break;
                    case StartupCheck.Credentials: _credentials = true; break;
                    case StartupCheck.Capabilities: _capabilities = true; break;
                    case StartupCheck.Pipe: _pipe = true; break;
                    case StartupCheck.Loop: _loop = true; break;
                    default: throw new ArgumentOutOfRangeException(nameof(check));
                }
                if (!_written && !_suppressed && _identity && _credentials && _capabilities && _pipe && _loop)
                {
                    _written = true;
                    snapshot = Snapshot();
                }
            }
            if (snapshot != null) _write(snapshot);
        }

        public OperationalChecks Snapshot()
        {
            lock (_gate)
                return new OperationalChecks
                {
                    Identity = _identity,
                    Credentials = _credentials,
                    Capabilities = _capabilities,
                    Pipe = _pipe,
                    Loop = _loop,
                };
        }
    }

    public static class HealthCheck
    {
        // Yeni ajanlarda phase=operational zorunludur. Phase alanı olmayan eski sürümlerde
        // (özellikle geri dönüşte kurulan 0.1.11 ve öncesi) version + ts kuralı korunur.
        public static bool IsHealthy(string json, string expectedVersion, DateTime notBeforeUtc)
        {
            if (string.IsNullOrWhiteSpace(json) || string.IsNullOrWhiteSpace(expectedVersion)) return false;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string? actual = root.TryGetProperty("version", out JsonElement version) && version.ValueKind == JsonValueKind.String
                    ? version.GetString()?.TrimStart('v', 'V') : null;
                if (!string.Equals(actual, expectedVersion.Trim().TrimStart('v', 'V'), StringComparison.OrdinalIgnoreCase)) return false;
                if (!root.TryGetProperty("ts", out JsonElement ts) || !ts.TryGetInt64(out long unix)) return false;
                if (DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime < notBeforeUtc) return false;
                if (!root.TryGetProperty("phase", out JsonElement phase)) return true;
                return phase.ValueKind == JsonValueKind.String && string.Equals(phase.GetString(), "operational", StringComparison.Ordinal);
            }
            catch (JsonException) { return false; }
            catch (ArgumentOutOfRangeException) { return false; }
        }
    }
}
