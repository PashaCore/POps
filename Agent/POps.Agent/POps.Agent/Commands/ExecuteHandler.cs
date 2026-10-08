using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // execute: yerel terminal yeteneği ve sınıfın terminal modülü açıksa komut arka planda çalışır (bkz. CommandRunner),
    // sonuç result ile (bkz. ResultOutbox). cancel_task: çalışan komut, güç işleminin geri sayımı ya da okundu onayı
    // beklenen mesaj iptal edilir.
    [SupportedOSPlatform("windows")]
    internal sealed class ExecuteHandler : ICommandHandler
    {
        private readonly Func<CommandRunner> _runner;
        private readonly CapabilityGate _gate;
        private readonly ResultOutbox _outbox;
        private readonly Func<string?> _hwId;
        private readonly Action<LocalAuditEvent> _audit;
        private readonly PowerActions _power;
        private readonly UserMessages _messages;

        // Çalıştırıcı testlerde sahtesiyle değiştirilir, kimlik set_identity ile değişir: her kullanımda okunur
        public ExecuteHandler(Func<CommandRunner> runner, CapabilityGate gate, ResultOutbox outbox, Func<string?> hwId,
            Action<LocalAuditEvent> audit, PowerActions power, UserMessages messages)
        {
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _power = power ?? throw new ArgumentNullException(nameof(power));
            _messages = messages ?? throw new ArgumentNullException(nameof(messages));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "execute", "cancel_task" };

        public Task HandleAsync(ServerCommand command) =>
            command.Action == "execute" ? HandleExecuteAsync(command.Root, command.Stopping) : HandleCancelTaskAsync(command.Root);

        private async Task HandleExecuteAsync(JsonElement root, CancellationToken stoppingToken)
        {
            CommandPermission commandPermission = CommandExecutionPolicy.Permission(AgentCapabilities.TerminalEnabled, _gate.ModuleEnabled(AgentModules.Terminal));
            if (!commandPermission.Allowed)
            {
                // Görev "Running"de asılı kalmasın diye sonuç olarak da bildirilir. Çıkış kodu -5 (reddedildi): eski sunucu
                // bunu Completed değil Failed sayar; yeni sunucu capability_denied ile Denied yapar.
                int tid = root.GetProperty("task_id").GetInt32();
                await _outbox.SendResultAsync(tid, new { type = "result", pc_name = _hwId(), task_id = tid, output = commandPermission.Rejection, exit_code = CommandRunner.ExitDenied });
                await _gate.DenyAsync("terminal", "execute", tid, commandPermission.Reason);
            }
            else
            {
                string? cmd = root.GetProperty("script_path").GetString();
                int tid = root.GetProperty("task_id").GetInt32();
                string? requestedBy = root.TryGetProperty("requested_by", out var requestedByProperty) && requestedByProperty.ValueKind == JsonValueKind.String
                    ? requestedByProperty.GetString() : null;
                CommandRunner runner = _runner();
                // Aynı görev zaten çalışıyorsa (sunucu emri yeniden gönderdi) ikinci kez çalıştırılmaz; sonuç ilk çalıştırmadan
                // gelir. Sunucuya ayrıca bir şey gönderilmez: "yinelenen" sonucu çalışan görevin kaydının üzerine yazardı.
                if (runner.IsRunning(tid))
                {
                    POpsHelpers.Log("AGENT", $"Uzaktan komut zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: {tid}).");
                    return;
                }
                POpsHelpers.Log("AGENT", $"Uzaktan komut çalıştırılıyor (TaskID: {tid})");
                _audit(LocalAudit.CommandStarted(tid, cmd, requestedBy));
                // Görev kimliği burada (eşzamanlı olarak) ayrılır: arkasından gelen aynı kimlik ikinci işlem başlatamaz
                Task<CommandExecutionResult> run = runner.RunAsync(tid, cmd, stoppingToken);
                _ = Task.Run(async () =>
                {
                    CommandExecutionResult execution = await run;
                    if (execution.ExitCode == CommandRunner.ExitDuplicate)
                    {
                        POpsHelpers.Log("AGENT", $"Uzaktan komut zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: {tid}).");
                        return;
                    }
                    _audit(LocalAudit.CommandFinished(tid, execution.ExitCode, execution.Duration));
                    // Sonuç o anki bağlantıdan gider; bağlantı koptuysa sırada (onaylı sunucuda diskte) bekler
                    await _outbox.SendResultAsync(tid, new
                    {
                        type = "result",
                        pc_name = _hwId(),
                        output = execution.Output,
                        task_id = tid,
                        exit_code = execution.ExitCode,
                    });
                }, CancellationToken.None);
            }
        }

        private async Task HandleCancelTaskAsync(JsonElement root)
        {
            int cancelId = root.TryGetProperty("task_id", out var cancelProp) && cancelProp.ValueKind == JsonValueKind.Number
                && cancelProp.TryGetInt32(out int cancelTaskId) ? cancelTaskId : -1;
            if (_runner().Cancel(cancelId))
                POpsHelpers.Log("AGENT", $"Uzaktan komut panelden iptal edildi; işlem sonlandırılıyor (TaskID: {cancelId}).");
            // Güç işleminin geri sayımı ya da okundu onayı beklenen mesaj (sonuç -2)
            else if (_power.Cancel(cancelId))
                POpsHelpers.Log("AGENT", $"Güç işlemi panelden iptal edildi; geri sayım durduruldu (TaskID: {cancelId}).");
            else await _messages.CancelAsync(cancelId);
        }
    }
}
