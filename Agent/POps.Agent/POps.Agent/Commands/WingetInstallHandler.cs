using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // winget_install (bkz. WingetInstall). Retler "result" olarak bildirilir (-5; winget yoksa -7) ve hiçbir şey
    // çalıştırılmaz. Yerel terminal yeteneği ya da sınıfın deploy modülü kapalıysa ardından capability_denied gider
    // (execute ile aynı sıra). Çalıştırma execute gibi: aynı görev iki kez başlamaz, süre sınırı, cancel_task, sonuç
    // result_ack'e kadar saklanır. Kapsamsız yeniden deneme aynı görevin parçasıdır (bkz. WingetInstall.RetryArguments).
    [SupportedOSPlatform("windows")]
    internal sealed class WingetInstallHandler : ICommandHandler
    {
        private readonly Func<CommandRunner> _runner;
        private readonly CapabilityGate _gate;
        private readonly ResultOutbox _outbox;
        private readonly Func<string?> _hwId;
        private readonly Action<LocalAuditEvent> _audit;
        private readonly CapabilityState _capabilities;
        private readonly Func<string?> _locator;

        // Çalıştırıcı testlerde sahtesiyle değiştirilir, kimlik set_identity ile değişir: her kullanımda okunur.
        // capabilities: bağlamın yetenek durumu; locator: winget.exe'nin yeri, her emirde sorulur (null: yok; bkz.
        // MachineSeams.WingetLocator)
        public WingetInstallHandler(Func<CommandRunner> runner, CapabilityGate gate, ResultOutbox outbox, Func<string?> hwId,
            Action<LocalAuditEvent> audit, CapabilityState capabilities, Func<string?> locator)
        {
            _locator = locator ?? throw new ArgumentNullException(nameof(locator));
            _runner = runner ?? throw new ArgumentNullException(nameof(runner));
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _capabilities = capabilities ?? throw new ArgumentNullException(nameof(capabilities));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { WingetInstall.Action };

        public Task HandleAsync(ServerCommand command) => HandleWingetInstallAsync(command.Root, command.Stopping);

        private async Task HandleWingetInstallAsync(JsonElement root, CancellationToken stoppingToken)
        {
            if (!root.TryGetProperty("task_id", out JsonElement taskProp) || taskProp.ValueKind != JsonValueKind.Number || !taskProp.TryGetInt32(out int tid))
            {
                POpsHelpers.Log("AGENT", "winget_install emrinde geçerli task_id yok; yok sayıldı.", true);
                return;
            }
            string? requestedBy = root.TryGetProperty("requested_by", out JsonElement by) && by.ValueKind == JsonValueKind.String ? by.GetString() : null;
            CommandRunner runner = _runner();
            if (runner.IsRunning(tid))
            {
                POpsHelpers.Log("AGENT", $"winget kurulumu zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: {tid}).");
                return;
            }
            string? rejection = null, deniedCapability = null, deniedReason = null, wingetPath = null, version = null;
            string id = "";
            int exitCode = CommandRunner.ExitDenied;
            if (!_capabilities.TerminalEnabled)
            {
                rejection = WingetInstall.TerminalOffMessage;
                deniedCapability = "terminal";
            }
            else if (!_gate.ModuleEnabled(AgentModules.Deploy))
            {
                rejection = WingetInstall.DeployOffMessage;
                deniedCapability = AgentModules.Deploy;
                deniedReason = AgentModules.DisabledReason;
            }
            else if (!WingetInstall.TryParse(root, out id, out version)) rejection = WingetInstall.InvalidMessage;
            else if ((wingetPath = _locator()) == null)
            {
                rejection = WingetInstall.MissingMessage;
                exitCode = WingetInstall.ExitMissing;
            }
            if (rejection != null)
            {
                POpsHelpers.Log("AGENT", $"{rejection} (TaskID: {tid})", true);
                await _outbox.SendResultAsync(tid, new { type = "result", pc_name = _hwId(), task_id = tid, output = rejection, exit_code = exitCode });
                if (deniedCapability != null) await _gate.DenyAsync(deniedCapability, WingetInstall.Action, tid, deniedReason);
                return;
            }
            POpsHelpers.Log("AGENT", $"winget kurulumu başlıyor: {WingetInstall.Describe(id, version)} (TaskID: {tid})");
            _audit(LocalAudit.CommandStarted(tid, WingetInstall.Describe(id, version), requestedBy));
            // Görev kimliği burada (eşzamanlı olarak) ayrılır: arkasından gelen aynı kimlik ikinci işlem başlatamaz. Machine
            // kapsamında uygulanabilir yükleyici yoksa aynı görevde (kimlik ayrılı, aynı iptal ve süre sınırı) bir kez kapsamsız
            // yeniden denenir; sonuç bir tanedir, ikinci denemenin çıkış koduyla.
            CommandExecutionResult? machineAttempt = null;
            Task<CommandExecutionResult> run = runner.RunProgramAsync(tid, wingetPath, WingetInstall.Arguments(id, version), first =>
            {
                List<string>? retry = WingetInstall.RetryArguments(first.ExitCode, id, version);
                if (retry == null) return null;
                machineAttempt = first;
                POpsHelpers.Log("AGENT", $"winget: paketin machine kapsamında uygulanabilir yükleyicisi yok ({WingetInstall.ExitNoApplicableInstaller}); --scope olmadan yeniden deneniyor (TaskID: {tid}).");
                return retry;
            }, stoppingToken);
            _ = Task.Run(async () =>
            {
                CommandExecutionResult execution = await run;
                if (execution.ExitCode == CommandRunner.ExitDuplicate)
                {
                    POpsHelpers.Log("AGENT", $"winget kurulumu zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: {tid}).");
                    return;
                }
                TimeSpan duration = execution.Duration + (machineAttempt?.Duration ?? TimeSpan.Zero);
                _audit(LocalAudit.CommandFinished(tid, execution.ExitCode, duration));
                await _outbox.SendResultAsync(tid, new
                {
                    type = "result",
                    pc_name = _hwId(),
                    output = WingetInstall.ResultOutput(execution.Output, machineAttempt?.Output),
                    task_id = tid,
                    exit_code = execution.ExitCode,
                });
            }, CancellationToken.None);
        }
    }
}
