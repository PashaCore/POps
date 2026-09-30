using System;

#nullable disable

namespace POpsAgent
{
    public sealed class CommandPermission
    {
        public bool Allowed { get; init; }
        public string Rejection { get; init; }
    }

    public static class CommandExecutionPolicy
    {
        public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(30);
        public const int MaxOutputChars = 512 * 1024;
        public const string DisabledMessage = "[REDDEDİLDİ] Bu cihazda uzaktan terminal kapalı (yetenek politikası); komut çalıştırılmadı.";

        public static CommandPermission Permission(bool terminalEnabled) => terminalEnabled
            ? new CommandPermission { Allowed = true }
            : new CommandPermission { Allowed = false, Rejection = DisabledMessage };

        public static TimeSpan RemainingTimeout(DateTimeOffset startedAt, DateTimeOffset now)
        {
            TimeSpan remaining = MaxDuration - (now - startedAt);
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        public static string TruncateOutput(string output)
        {
            output ??= "";
            if (output.Length <= MaxOutputChars) return output;
            string suffix = $"\n[ÇIKTI KISALTILDI: toplam {output.Length} karakter]";
            return output.Substring(0, MaxOutputChars - suffix.Length) + suffix;
        }
    }
}
