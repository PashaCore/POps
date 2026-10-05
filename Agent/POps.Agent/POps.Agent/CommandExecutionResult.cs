using System;

#nullable disable

namespace POpsAgent
{
    public sealed class CommandExecutionResult
    {
        public CommandExecutionResult(string output, int exitCode, TimeSpan duration)
        {
            Output = CommandExecutionPolicy.TruncateOutput(output);
            ExitCode = exitCode;
            Duration = duration;
        }

        public string Output { get; }
        public int ExitCode { get; }
        public TimeSpan Duration { get; }
    }
}
