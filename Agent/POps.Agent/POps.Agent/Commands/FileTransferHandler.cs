using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // file_push / file_pull: yöneticinin dosya gönderimi ve dosya alması (bkz. FileTransfer)
    [SupportedOSPlatform("windows")]
    internal sealed class FileTransferHandler : ICommandHandler
    {
        private readonly CapabilityGate _gate;
        private readonly string _serverUrl;
        private readonly Func<string> _hwId;
        private readonly Func<object, Task<bool>> _send;
        private readonly Action<string> _toTray;
        private readonly Action<LocalAuditEvent> _audit;

        // send: komut kanalına gönderim; toTray: tepsiye mesaj. Kimlik set_identity ile değişir, gönderim ve tepsi
        // testlerde sahteleriyle değiştirilir: her kullanımda okunur.
        public FileTransferHandler(CapabilityGate gate, string serverUrl, Func<string> hwId, Func<object, Task<bool>> send,
            Action<string> toTray, Action<LocalAuditEvent> audit)
        {
            _gate = gate ?? throw new ArgumentNullException(nameof(gate));
            _serverUrl = serverUrl;
            _hwId = hwId ?? throw new ArgumentNullException(nameof(hwId));
            _send = send ?? throw new ArgumentNullException(nameof(send));
            _toTray = toTray ?? throw new ArgumentNullException(nameof(toTray));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        }

        public IReadOnlyList<string> Actions { get; } = new[] { "file_push", "file_pull" };

        public Task HandleAsync(ServerCommand command) => HandleFileTransferAsync(command.Action, command.Root, command.Stopping);

        // Emir doğrulanır ve iş arka planda yürür (komut döngüsünü bekletmez); sonuç file_result ile bildirilir.
        // Yetenek kapalıysa yalnızca capability_denied gider (transfer_id ile; sunucu aktarımı ondan "rejected" yapar).
        // transfer_id eksik ya da geçersizse file_result gönderilmez (sunucu bilmediği aktarımı yok sayar), yalnızca loglanır.
        internal async Task HandleFileTransferAsync(string action, JsonElement root, CancellationToken token)
        {
            string? transferId = FileTransfer.TransferIdOf(root);
            if (!AgentCapabilities.FilesEnabled)
            {
                await _gate.DenyAsync("files", action, transferId: transferId);
                return;
            }
            if (transferId == null)
            {
                POpsHelpers.Log("FILES", $"{action} yok sayıldı: transfer_id eksik ya da geçersiz.", true);
                return;
            }
            if (action == "file_push")
            {
                if (!FileTransfer.TryParsePush(root, _serverUrl, out FileTransfer.PushRequest? push, out string? error))
                {
                    POpsHelpers.Log("FILES", $"Dosya gönderimi reddedildi ({transferId}): {error}.", true);
                    await _send(FileTransfer.Result(transferId, "rejected", detail: error));
                    return;
                }
                FileTransferTask = Task.Run(async () =>
                {
                    var (outcome, path, detail) = await FileTransfer.PushAsync(push, _hwId(), DateTime.Now, token);
                    if (outcome == "done")
                    {
                        _audit(LocalAudit.FilePushed(push.TransferId, path, push.Size, push.Sha256, push.Reason));
                        POpsHelpers.Log("FILES", $"Yönetici dosya gönderdi: {path} ({push.Size} bayt).");
                        _toTray("FILE_PUSHED:" + Path.GetFileName(path));
                    }
                    else POpsHelpers.Log("FILES", $"Dosya gönderimi tamamlanmadı ({push.TransferId}, {outcome}): {detail}.", true);
                    await _send(FileTransfer.Result(push.TransferId, outcome, path, detail));
                }, CancellationToken.None);
                return;
            }
            if (!FileTransfer.TryParsePull(root, _serverUrl, out FileTransfer.PullRequest? pull, out string? pullError))
            {
                POpsHelpers.Log("FILES", $"Dosya alma reddedildi ({transferId}): {pullError}.", true);
                await _send(FileTransfer.Result(transferId, "rejected", detail: pullError));
                return;
            }
            FileTransferTask = Task.Run(async () =>
            {
                var (outcome, path, detail, size) = await FileTransfer.PullAsync(pull, _hwId(), token);
                if (outcome == "done")
                {
                    _audit(LocalAudit.FilePulled(pull.TransferId, path, size, pull.Reason));
                    POpsHelpers.Log("FILES", $"Yönetici dosyayı aldı: {path} ({size} bayt).");
                    _toTray("FILE_PULLED:" + path);
                }
                else POpsHelpers.Log("FILES", $"Dosya alma tamamlanmadı ({pull.TransferId}, {outcome}): {detail}.", true);
                await _send(FileTransfer.Result(pull.TransferId, outcome, path, detail));
            }, CancellationToken.None);
        }

        // Testler: son başlatılan aktarım
        internal Task FileTransferTask { get; private set; } = Task.CompletedTask;
    }
}
