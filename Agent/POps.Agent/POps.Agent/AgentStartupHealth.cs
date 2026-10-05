using System;

#nullable disable

namespace POpsAgent
{
    public sealed class AgentStartupHealth
    {
        private readonly OperationalHealthGate _gate;

        public AgentStartupHealth(bool suppressed, Action<OperationalChecks> writer = null)
        {
            _gate = new OperationalHealthGate(suppressed, writer ?? AgentUpdate.WriteOperationalHealth);
        }

        public void Run(StartupCheck check, Action action) => _gate.Run(check, action);
        public void Mark(StartupCheck check) => _gate.Mark(check);
        public OperationalChecks Snapshot() => _gate.Snapshot();
    }
}
