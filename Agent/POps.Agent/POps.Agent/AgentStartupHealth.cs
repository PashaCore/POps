using System;

#nullable disable

namespace POpsAgent
{
    public sealed class AgentStartupHealth
    {
        private readonly OperationalHealthGate _gate;

        // writer: health.json'u yazan (servis: AgentUpdate.WriteOperationalHealth ile AgentPaths.HealthPath'e)
        public AgentStartupHealth(bool suppressed, Action<OperationalChecks> writer)
        {
            _gate = new OperationalHealthGate(suppressed, writer ?? throw new ArgumentNullException(nameof(writer)));
        }

        public void Run(StartupCheck check, Action action) => _gate.Run(check, action);
        public void Mark(StartupCheck check) => _gate.Mark(check);
        public OperationalChecks Snapshot() => _gate.Snapshot();
    }
}
