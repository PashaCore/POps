using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace POpsAgent
{
    // Bir görevin sırayla çalışan programları (CommandRunner.RunProgramsAsync kurar). Her program kabuksuz, bağımsız değişken
    // listesiyle çalışır (RunProgramAsync gibi). Görevin kimliği, iptali, süre sınırı ve servis durması hepsinde ortaktır: görev
    // durunca çalışan program sonlandırılır, sonraki RunAsync hiçbir şey başlatmadan durma sonucunu döner.
    public sealed class ProgramSteps
    {
        private readonly CommandRunner _runner;
        private readonly int _taskId;
        private readonly CancellationTokenSource _cancel;
        private readonly CancellationToken _timeout;
        private readonly CancellationToken _serviceStopping;

        internal ProgramSteps(CommandRunner runner, int taskId, CancellationTokenSource cancel, CancellationToken timeout, CancellationToken serviceStopping)
        {
            _runner = runner;
            _taskId = taskId;
            _cancel = cancel;
            _timeout = timeout;
            _serviceStopping = serviceStopping;
        }

        // Görev durdu mu: cancel_task, servis durması ya da görevin süre sınırı
        public bool Stopped => _cancel.IsCancellationRequested || _serviceStopping.IsCancellationRequested || _timeout.IsCancellationRequested;

        // Son RunAsync kendi süre sınırına (limit) takıldı mı: program sonlandırıldı (ExitTimeout), görev sürüyor
        public bool LimitReached { get; private set; }

        // limit: yalnızca bu çalıştırmanın, görevinkinden kısa süre sınırı (ör. winget'in sürüm sorgusu); görevin kalan süresini
        // de aşamaz
        public async Task<CommandExecutionResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan? limit = null)
        {
            LimitReached = false;
            if (Stopped) return _runner.StoppedResult(_cancel, _serviceStopping);
            using var own = new CancellationTokenSource();
            if (limit is TimeSpan length) own.CancelAfter(length);
            CommandExecutionResult result = await _runner.RunStepAsync(_taskId, fileName, arguments, _cancel, limit ?? TimeSpan.Zero, _timeout,
                _serviceStopping, own.Token);
            LimitReached = result.ExitCode == CommandRunner.ExitTimeout && own.IsCancellationRequested && !Stopped;
            return result;
        }
    }
}
