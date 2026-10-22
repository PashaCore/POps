using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Ajanın gönderdiği JSON alan adları sunucunun Pydantic modelleriyle BİREBİR aynı olmalı (snake_case). Modeller
    // depodaki Backend/pops/models.py'den okunur: sunucu bir alanı değiştirirse bu test kırılır.
    public class ServerContractTests : TestBase
    {
        private static List<string> ModelFields(string className)
        {
            string[] lines = File.ReadAllLines(Path.Combine(TestEnvironment.RepoRoot(), "Backend", "pops", "models.py"));
            int start = Array.FindIndex(lines, l => l.StartsWith($"class {className}(BaseModel)", StringComparison.Ordinal));
            Assert.True(start >= 0, $"models.py'de {className} yok");
            var fields = new List<string>();
            for (int i = start + 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length > 0 && !char.IsWhiteSpace(line[0])) break;
                Match m = Regex.Match(line, @"^    (\w+)\s*:");
                if (m.Success) fields.Add(m.Groups[1].Value);
            }
            Assert.NotEmpty(fields);
            return fields;
        }

        private static List<string> JsonKeys(object payload)
        {
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(payload));
            return doc.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        }

        private static void AssertSameFields(string model, object payload) =>
            Assert.Equal(ModelFields(model).OrderBy(f => f, StringComparer.Ordinal), JsonKeys(payload).OrderBy(f => f, StringComparer.Ordinal));

        [Fact]
        public void SoftwareInventory_MatchesServerModel()
        {
            AssertSameFields("SoftwareInventoryInput", new SoftwareInventoryPayload());
            AssertSameFields("SoftwareItem", new SoftwareItem { Name = "x" });
        }

        [Fact]
        public void PatchStatus_MatchesServerModel()
        {
            AssertSameFields("PatchStatusInput", new PatchStatusPayload());
            AssertSameFields("PatchUpdateItem", new PatchUpdateItem { Title = "x" });
        }

        [Fact]
        public void PatchStatus_NullsAreSentAsNull()
        {
            // last_result / last_install / severity null giderse sunucu önceki değeri korur (COALESCE)
            using JsonDocument doc = JsonDocument.Parse(JsonSerializer.Serialize(PatchClassifier.BuildStatus(
                new List<PendingUpdate> { new PendingUpdate { Title = "t" } }, false, DateTime.UtcNow, null, null)));
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("last_result").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("last_install").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("updates")[0].GetProperty("kb").ValueKind);
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("updates")[0].GetProperty("severity").ValueKind);
            Assert.Equal(JsonValueKind.False, doc.RootElement.GetProperty("updates")[0].GetProperty("is_security").ValueKind);
        }

        [Fact]
        public void AuthEvents_MatchServerModel() => AssertSameFields("AuthEventInput", new AuthEventPayload());

        [Fact]
        public void AgentLog_MatchesServerModel() => AssertSameFields("LogInput", new AgentLogPayload());

        [Fact]
        public void PolicyAlert_MatchesServerModel() => AssertSameFields("PolicyAlertInput", new DnsPolicyMonitor.PolicyAlertPayload());

        [Fact]
        public void DevicePath_EscapesTheId()
        {
            Assert.Equal("/api/software/HW-678CC8C5265E", AgentHttp.DevicePath("/api/software/", "HW-678CC8C5265E"));
            Assert.Equal("/api/patches/a%2Fb", AgentHttp.DevicePath("/api/patches/", "a/b"));
        }
    }
}
