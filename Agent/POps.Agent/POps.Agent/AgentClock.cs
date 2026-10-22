using System;

namespace POpsAgent
{
    // Servisin saati (AgentContext.Clock): bilgisayarın saati TimeProvider'ın kendisinden (DateTimeOffset.UtcNow), monotonik saat
    // Environment.TickCount64'ten (ms; açılıştan beri, uyku ve hazırda bekleme süresini de sayar, bilgisayarın saati ayarlansa da
    // değişmez). Sınav modunun bitişi bununla sayılır (bkz. ExamMode.TimeLeftAsync): kapağı kapatılan bir dizüstünde de sınav
    // süresinde biter. Testler kendi sahte saatini verir (bkz. POps.Tests.AgentHarness).
    public sealed class AgentClock : TimeProvider
    {
        public static AgentClock Instance { get; } = new AgentClock();

        private AgentClock() { }

        public override long GetTimestamp() => Environment.TickCount64;

        public override long TimestampFrequency => 1000;
    }
}
