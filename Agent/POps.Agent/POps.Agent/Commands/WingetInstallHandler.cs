using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // winget_install (bkz. WingetInstall). Retler "result" olarak bildirilir (-5; winget yoksa -7) ve hiçbir şey
    // çalıştırılmaz. Yerel terminal yeteneği ya da sınıfın deploy modülü kapalıysa ardından capability_denied gider
    // (execute ile aynı sıra). Çalıştırma execute gibi: aynı görev iki kez başlamaz, süre sınırı, cancel_task, sonuç
    // result_ack'e kadar saklanır. Sürüm sorgusu ve kapsamsız yeniden deneme aynı görevin parçasıdır (bkz.
    // WingetInstall.LatestVersion, WingetInstall.RetryArguments).
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

        // Sürüm sorgusunun kendi süre sınırı (testler kısaltır)
        internal TimeSpan QueryLimit { get; set; } = WingetInstall.QueryLimit;

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
            // Görev kimliği burada (eşzamanlı olarak) ayrılır: arkasından gelen aynı kimlik ikinci işlem başlatamaz. Bütün adımlar
            // (sürüm sorgusu, kurulum, kapsamsız yeniden deneme) aynı görevdir: kimlik ayrılı, aynı iptal ve süre sınırı; sonuç bir
            // tanedir, son kurulum denemesinin çıkış koduyla.
            var clock = Stopwatch.StartNew();
            var install = new InstallSteps(tid, wingetPath!, id, version, QueryLimit);
            Task<CommandExecutionResult> run = runner.RunProgramsAsync(tid, install.RunAsync, stoppingToken);
            _ = Task.Run(async () =>
            {
                CommandExecutionResult execution = await run;
                if (execution.ExitCode == CommandRunner.ExitDuplicate)
                {
                    POpsHelpers.Log("AGENT", $"winget kurulumu zaten çalışıyor; yinelenen emir yok sayıldı (TaskID: {tid}).");
                    return;
                }
                _audit(LocalAudit.CommandFinished(tid, execution.ExitCode, clock.Elapsed));
                await _outbox.SendResultAsync(tid, new
                {
                    type = "result",
                    pc_name = _hwId(),
                    output = install.LookupStopped ? WingetInstall.LookupStoppedOutput(execution.Output)
                        : WingetInstall.ResultOutput(execution.Output, install.MachineAttempt?.Output, install.VersionNote),
                    task_id = tid,
                    exit_code = execution.ExitCode,
                });
            }, CancellationToken.None);
        }

        // Bir winget_install görevinin adımları (CommandRunner.RunProgramsAsync içinde, sırayla) ve sonuca giren parçaları
        private sealed class InstallSteps
        {
            private readonly int _tid;
            private readonly string _winget;
            private readonly string _id;
            private readonly string? _version;
            private readonly TimeSpan _queryLimit;

            public InstallSteps(int tid, string winget, string id, string? version, TimeSpan queryLimit)
            {
                _tid = tid;
                _winget = winget;
                _id = id;
                _version = version;
                _queryLimit = queryLimit;
            }

            // Sürümü ajan seçtiyse çıktının ikinci satırı (WingetInstall.PinnedNote ya da UnpinnedNote)
            public string? VersionNote { get; private set; }
            // Görev sürüm sorgusu sırasında durdu: kurulum çalışmadı
            public bool LookupStopped { get; private set; }
            // Kapsamsız yeniden denendiyse ilk denemenin (--scope machine) sonucu
            public CommandExecutionResult? MachineAttempt { get; private set; }

            public async Task<CommandExecutionResult> RunAsync(ProgramSteps steps)
            {
                string? version = _version;
                if (version == null)
                {
                    // Sürüm verilmedi: kaynaktaki en yeni sürüme sabitlenir; o sürümün machine yükleyicisi yoksa aşağıdaki kapsamsız
                    // deneme çalışır (sabitlenmeseydi winget machine yükleyicisi olan eski bir sürümü seçerdi)
                    CommandExecutionResult lookup = await steps.RunAsync(_winget, WingetInstall.VersionsArguments(_id), _queryLimit);
                    if (steps.Stopped)
                    {
                        LookupStopped = true;
                        return lookup;
                    }
                    string? reason = WingetInstall.QueryFailure("show", lookup.ExitCode, steps.LimitReached, _queryLimit);
                    version = reason == null ? WingetInstall.LatestVersion(lookup.Output, out reason) : null;
                    VersionNote = version != null ? WingetInstall.PinnedNote(version) : WingetInstall.UnpinnedNote(reason ?? "");
                    if (version != null)
                        POpsHelpers.Log("AGENT", $"winget: kaynaktaki en yeni sürüm {version}; kurulum bu sürüme sabitlendi (TaskID: {_tid}).");
                    else
                        POpsHelpers.Log("AGENT", $"winget: en yeni sürüm bulunamadı ({reason}); sürüm verilmeden kuruluyor (TaskID: {_tid}).", true);
                }
                CommandExecutionResult first = await steps.RunAsync(_winget, WingetInstall.Arguments(_id, version));
                List<string>? retry = WingetInstall.RetryArguments(first.ExitCode, _id, version);
                if (retry == null) return first;
                MachineAttempt = first;
                POpsHelpers.Log("AGENT", $"winget: paketin machine kapsamında uygulanabilir yükleyicisi yok ({WingetInstall.ExitNoApplicableInstaller}); --scope olmadan yeniden deneniyor (TaskID: {_tid}).");
                return await steps.RunAsync(_winget, retry);
            }
        }
    }
}
