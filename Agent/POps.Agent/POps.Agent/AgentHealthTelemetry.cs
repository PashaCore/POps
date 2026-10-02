using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using POps.Shared;

#nullable disable

namespace POpsAgent
{
    public sealed class AgentHealthSnapshot
    {
        [JsonPropertyName("started_at")] public long StartedAt { get; init; }
        [JsonPropertyName("last_policy_sync")] public long? LastPolicySync { get; init; }
        [JsonPropertyName("last_inventory_upload")] public long? LastInventoryUpload { get; init; }
        [JsonPropertyName("tray_connected")] public bool TrayConnected { get; init; }
        [JsonPropertyName("vision_channel")] public string VisionChannel { get; init; }
        [JsonPropertyName("loop_errors_1h")] public int LoopErrors1h { get; init; }
        [JsonPropertyName("last_error")] public string LastError { get; init; }
        // Karantina (0.1.13+): kilit ekranı ve ağ yalıtımı ayrı ayrı; yalıtım uygulanamadıysa nedeni
        [JsonPropertyName("screen_locked")] public bool ScreenLocked { get; init; }
        [JsonPropertyName("network_isolated")] public bool NetworkIsolated { get; init; }
        [JsonPropertyName("isolation_error")] public string IsolationError { get; init; }
    }

    // Son bir saatin döngü hataları ve son başarılı arka plan işlemleri. Bellek içidir; heartbeat gözlemi içindir.
    public sealed class AgentHealthTelemetry
    {
        private static readonly TimeSpan ErrorWindow = TimeSpan.FromHours(1);
        private readonly object _sync = new object();
        private readonly Func<DateTimeOffset> _now;
        private readonly Queue<DateTimeOffset> _errors = new Queue<DateTimeOffset>();
        private readonly long _startedAt;
        private long? _lastPolicySync;
        private long? _lastInventoryUpload;
        private string _lastError = "";

        public AgentHealthTelemetry(Func<DateTimeOffset> now = null)
        {
            _now = now ?? (() => DateTimeOffset.UtcNow);
            _startedAt = _now().ToUnixTimeSeconds();
        }

        public void PolicySynced()
        {
            lock (_sync) _lastPolicySync = _now().ToUnixTimeSeconds();
        }

        public void InventoryUploaded()
        {
            lock (_sync) _lastInventoryUpload = _now().ToUnixTimeSeconds();
        }

        public void RecordError(string loop, string error)
        {
            lock (_sync)
            {
                DateTimeOffset now = _now();
                Prune(now);
                _errors.Enqueue(now);
                // LogText.Safe kesildiğinde sona üç nokta ekler; 199 + işaret = en çok 200 karakter.
                _lastError = LogText.Safe((loop ?? "loop") + ": " + (error ?? "(yok)"), 199);
            }
        }

        public AgentHealthSnapshot Snapshot(bool trayConnected, bool visionEnabled, bool visionConnected)
            => Snapshot(trayConnected, visionEnabled, visionConnected, false, false, null);

        public AgentHealthSnapshot Snapshot(bool trayConnected, bool visionEnabled, bool visionConnected,
            bool screenLocked, bool networkIsolated, string isolationError)
        {
            lock (_sync)
            {
                Prune(_now());
                return new AgentHealthSnapshot
                {
                    StartedAt = _startedAt,
                    LastPolicySync = _lastPolicySync,
                    LastInventoryUpload = _lastInventoryUpload,
                    TrayConnected = trayConnected,
                    VisionChannel = VisionState(visionEnabled, visionConnected),
                    LoopErrors1h = _errors.Count,
                    LastError = _lastError,
                    ScreenLocked = screenLocked,
                    NetworkIsolated = networkIsolated,
                    IsolationError = string.IsNullOrEmpty(isolationError) ? null : LogText.Safe(isolationError, 199),
                };
            }
        }

        public static string VisionState(bool enabled, bool connected) => !enabled ? "off" : connected ? "connected" : "idle";

        private void Prune(DateTimeOffset now)
        {
            while (_errors.Count > 0 && now - _errors.Peek() >= ErrorWindow) _errors.Dequeue();
        }
    }
}
