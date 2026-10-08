using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Komut tablosu (bkz. CommandDispatcher; Worker.cs bölmesinin a1 adımı). Eski if/else zincirinin işlediği her eylem
    // tabloda, komut kanalının her sunucu mesajının bir işleyicisi var, aynı eylem iki işleyicide olamaz, tanınmayan eylem
    // yok sayılır.
    [Collection(SharedStateCollection.Name)]
    public class CommandDispatcherTests : SharedStateTestBase, IDisposable
    {
        // Worker.HandleServerMessageAsync'in if/else zincirinin (a1'den önce) işlediği eylemler, zincirdeki sırayla
        private static readonly string[] OldChainActions =
        {
            "execute", "get_hardware", "set_capabilities", "start_stream", "start_vision_session", "stop_stream",
            "update_agent", "winget_install", "wake_peer", "set_identity", "set_secret", "set_bypass_secret", "exam_mode",
            "file_push", "file_pull", "cancel_task", "power", "user_message", "lockdown", "unlock", "server_info",
            "update_result_ack", "result_ack", "scan_updates", "install_updates",
        };

        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<string> _tray = new List<string>();

        public CommandDispatcherTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("dispatcher-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("dispatcher-data");
            AgentCapabilities.Load();
            _worker = new Worker(NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context)
            {
                HwId = "HW-DISPATCH",
                SendOverride = payload =>
                {
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(payload));
                    return Task.FromResult(true);
                },
                TrayOverride = message => { lock (_tray) _tray.Add(message); },
            };
        }

        public void Dispose()
        {
            _worker.Dispose();
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        [Fact]
        public void Table_HasExactlyTheActionsOfTheOldChain()
        {
            Assert.Equal(OldChainActions.Length, OldChainActions.Distinct(StringComparer.Ordinal).Count());
            Assert.Equal(OldChainActions.OrderBy(a => a, StringComparer.Ordinal), _worker.Dispatcher.Actions.OrderBy(a => a, StringComparer.Ordinal));
        }

        // docs/protocol/server-to-agent: komut kanalında (/ws/agent) gelen her eylemin işleyicisi var; tabloda şemasız
        // eylem yok. "type": "remote_input" action'a bakılmadan kendi işleyicisine gider.
        [Fact]
        public void EveryCommandChannelMessage_HasAHandler()
        {
            var actions = new List<string>();
            bool remoteInput = false;
            foreach (string file in Directory.GetFiles(Path.Combine(TestEnvironment.RepoRoot(), "docs", "protocol", "server-to-agent"), "*.json"))
            {
                JsonElement schema = JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
                if (!schema.GetProperty("x-pops-channels").EnumerateArray().Any(c => c.GetString() == "/ws/agent")) continue;
                JsonElement properties = schema.GetProperty("properties");
                if (properties.TryGetProperty("action", out JsonElement action) && action.TryGetProperty("const", out JsonElement name))
                    actions.Add(name.GetString());
                else
                    remoteInput |= properties.GetProperty("type").GetProperty("const").GetString() == CommandDispatcher.RemoteInput;
            }
            Assert.True(remoteInput);
            Assert.All(actions, a => Assert.NotNull(_worker.Dispatcher.HandlerFor(a)));
            Assert.Equal(actions.OrderBy(a => a, StringComparer.Ordinal), _worker.Dispatcher.Actions.OrderBy(a => a, StringComparer.Ordinal));
        }

        [Fact]
        public void SameActionInTwoHandlers_Fails()
        {
            ICommandHandler remote = new DelegateHandler(_ => Task.CompletedTask, CommandDispatcher.RemoteInput);
            Assert.Throws<ArgumentException>(() => new CommandDispatcher(new ICommandHandler[]
            {
                new DelegateHandler(_ => Task.CompletedTask, "lockdown", "unlock"),
                new DelegateHandler(_ => Task.CompletedTask, "unlock"),
            }, remote));
        }

        [Fact]
        public async Task UnknownAction_IsIgnored_RemoteInputGoesToItsHandler()
        {
            var calls = new List<string>();
            Task Record(ServerCommand command, string handler)
            {
                calls.Add(handler + ":" + command.Action);
                return Task.CompletedTask;
            }
            var dispatcher = new CommandDispatcher(new ICommandHandler[] { new DelegateHandler(c => Record(c, "known"), "known") },
                new DelegateHandler(c => Record(c, "remote"), CommandDispatcher.RemoteInput));

            foreach (string message in new[] { "{\"action\":\"teleport\"}", "{\"action\":null}", "{\"type\":\"something_else\"}", "{}", "{\"action\":\"Known\"}" })
                await dispatcher.DispatchAsync(message, null, CancellationToken.None);
            Assert.Empty(calls);

            await dispatcher.DispatchAsync("{\"action\":\"known\"}", null, CancellationToken.None);
            await dispatcher.DispatchAsync("{\"type\":\"remote_input\",\"action\":\"known\",\"device\":\"HW-1\"}", null, CancellationToken.None);
            Assert.Equal(new[] { "known:known", "remote:remote_input" }, calls);

            // Worker: tanınmayan eylem hiçbir şey göndermez, tepsiye de bir şey gitmez
            await _worker.HandleServerMessageAsync("{\"action\":\"teleport\",\"to\":\"mars\"}", null, CancellationToken.None);
            Assert.Empty(_sent);
            Assert.Empty(_tray);
        }
    }
}
