namespace POpsAgent
{
    // X-Agent-Features: /ws/agent bağlantısında duyurulan, ajanın uyguladığı özellikler (virgülle ayrılmış, [a-z0-9_]).
    // Sunucu bu eylemleri yalnızca duyuran ajana gönderir; bilmeyen sunucu başlığı yok sayar.
    public static class AgentFeatures
    {
        public const string HeaderName = "X-Agent-Features";

        public static readonly string[] All =
        {
            "exam",
            "files",
            "winget",
        };

        public static string Header => string.Join(",", All);
    }
}
