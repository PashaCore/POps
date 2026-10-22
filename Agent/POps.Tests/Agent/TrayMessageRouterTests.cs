using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using POps.Shared;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Tepsi borusunun mesajları uçtan uca (Worker.cs bölmesinin a5 adımında TrayMessageRouter'a taşındı; bu testler
    // taşımadan önce de aynı sonucu verir): tepsi bağlanınca gönderilenlerin sırası, tepsiden gelen mesajların etkisi ve
    // kopunca unutulanlar. Gerçek boru açılır (testlere özel ad); tepsinin yerine istemci doğrulamasını geçen sahte bir
    // istemci bağlanır. Sunucu yok: giden komut mesajları SendOverride'a yazılır.
    [Collection(SharedStateCollection.Name)]
    public class TrayMessageRouterTests : SharedStateTestBase, IDisposable
    {
        private const string HwId = "HW-TRAY00000001";

        private readonly Worker _worker;
        private readonly List<JsonElement> _sent = new List<JsonElement>();
        private readonly List<FakeTray> _trays = new List<FakeTray>();

        public TrayMessageRouterTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("tray-router-secure");
            AgentUpdate.DataDir = TestEnvironment.NewDir("tray-router-data");
            TrayPipeServer.PipeName = "POpsTrayPipe-test-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            TrayPipeServer.ClientCheckOverride = _ => null;
            // Okunamayan yapılandırma: tepsi bağlanınca CONFIG_ERROR da gönderilir
            string config = Path.Combine(TestEnvironment.NewDir("tray-router-config"), "appsettings.json");
            File.WriteAllText(config, "{bozuk");
            POpsHelpers.ConfigPaths = new[] { config };
            AgentCapabilities.Load();   // dosya yok: hepsi açık
            _worker = new Worker(NullLogger<Worker>.Instance, AgentHarness.FromStatics().Context)
            {
                HwId = HwId,
                SendOverride = p =>
                {
                    lock (_sent) _sent.Add(JsonSerializer.SerializeToElement(p));
                    return Task.FromResult(true);
                },
            };
            _worker.EnsureTrayPipeServer();
        }

        public void Dispose()
        {
            foreach (FakeTray tray in _trays) tray.Dispose();
            _worker.TrayPipe?.Stop();
            _worker.Dispose();
            AgentModules.Reset();
            POpsHelpers.ConfigPaths = new string[0];
            TrayPipeServer.PipeName = "POpsTrayPipe";
            TrayPipeServer.ClientCheckOverride = null;
            SecureStore.Dir = TestEnvironment.DefaultSecureDir;
            AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;
            AgentCapabilities.Load();
        }

        private async Task<FakeTray> ConnectTrayAsync()
        {
            FakeTray tray = await FakeTray.ConnectAsync(TrayPipeServer.PipeName);
            _trays.Add(tray);
            Assert.True(await FakeTray.WaitUntilAsync(() => _worker.TrayPipe.IsConnected), "tepsi bağlanmadı");
            return tray;
        }

        private static Task<bool> ReceivedAsync(FakeTray tray, string message) =>
            FakeTray.WaitUntilAsync(() => tray.Received.Contains(message));

        private string ActiveWindow() =>
            JsonSerializer.SerializeToElement(_worker.HeartbeatPayload()).GetProperty("active_window").GetString();

        private List<JsonElement> Results(int taskId)
        {
            lock (_sent)
                return _sent.Where(m => m.GetProperty("type").GetString() == "result" && m.GetProperty("task_id").GetInt32() == taskId).ToList();
        }

        private static string UserMessage(int taskId) =>
            "{\"action\":\"user_message\",\"task_id\":" + taskId + ",\"title\":\"Duyuru\",\"text\":\"Lütfen okuyun\",\"style\":\"info\",\"requires_ack\":true,\"requested_by\":\"Pasha\"}";

        // Tepsi bağlanınca sırayla: karantina durumu (kilit yok: eski kilit ekranı kapanır), süren sınavın bandı, yardım
        // masası menüsü, yapılandırma sorunu, sonra okundu onayı bekleyen mesajlar yeniden. Tepsi yeniden bağlanınca da aynı.
        [Fact]
        public async Task OnConnect_TheTrayGetsQuarantineExamHelpdeskConfigAndWaitingMessages_InThisOrder()
        {
            SecureStore.WriteProtected(ExamMode.StatePath, "{\"message\":\"Matematik sınavı\",\"until\":null,\"allow\":[],\"block_apps\":[]}");
            string exam = ExamHandler.ExamTrayMessage(ExamMode.Load());
            string config = _worker.ConfigErrorMessage();
            Assert.NotNull(config);

            FakeTray first = await ConnectTrayAsync();
            string[] sync = { QuarantineControl.UnlockMessage("sync"), exam, "HELPDESK_MENU:1", config };
            Assert.True(await FakeTray.WaitUntilAsync(() => first.Received.Count >= sync.Length));
            Assert.Equal(sync, first.Received.Take(sync.Length));

            // Okundu onayı bekleyen mesaj: tepsi yeniden bağlanınca en sonda yeniden gösterilir
            await _worker.HandleServerMessageAsync(UserMessage(71), null, CancellationToken.None);
            Assert.True(await FakeTray.WaitUntilAsync(() => first.Received.Any(m => m.StartsWith(UserMessages.ShowPrefix, StringComparison.Ordinal))));
            string shown = first.Received.Single(m => m.StartsWith(UserMessages.ShowPrefix, StringComparison.Ordinal));
            first.Dispose();
            Assert.True(await FakeTray.WaitUntilAsync(() => !_worker.TrayPipe.IsConnected), "tepsi kopmadı");

            FakeTray second = await ConnectTrayAsync();
            string[] again = sync.Append(shown).ToArray();
            Assert.True(await FakeTray.WaitUntilAsync(() => second.Received.Count >= again.Length));
            Assert.Equal(again, second.Received.Take(again.Length));
        }

        // Tepsinin okundu onayı (USER_MESSAGE_ACK:<task_id>) görevin sonucunu gönderir
        [Fact]
        public async Task UserMessageAcknowledgedInTheTray_ReportsRead()
        {
            FakeTray tray = await ConnectTrayAsync();
            await _worker.HandleServerMessageAsync(UserMessage(72), null, CancellationToken.None);
            Assert.True(await FakeTray.WaitUntilAsync(() => tray.Received.Any(m => m.StartsWith(UserMessages.ShowPrefix, StringComparison.Ordinal))));
            Assert.Empty(Results(72));

            await tray.SendAsync(UserMessages.AckPrefix + "72");
            Assert.True(await FakeTray.WaitUntilAsync(() => Results(72).Count == 1), "sonuç gönderilmedi");
            Assert.Equal(UserMessages.ReadOutput, Results(72)[0].GetProperty("output").GetString());
        }

        // Tepsi oturumu kapattı: ekran yakalama durur
        [Fact]
        public async Task StopVisionTunnel_StopsTheCapture()
        {
            FakeTray tray = await ConnectTrayAsync();
            await tray.SendAsync("STOP_VISION_TUNNEL");
            Assert.True(await ReceivedAsync(tray, "STOP_CAPTURE"));
        }

        // Ön plandaki uygulamanın yalnızca süreç adı heartbeat'e girer; pencere başlığı hiç girmez; tepsi kopunca unutulur
        [Fact]
        public async Task ActiveApp_GoesIntoTheHeartbeat_TheWindowTitleNever_ForgottenWhenTheTrayDisconnects()
        {
            Assert.Equal("-", ActiveWindow());
            FakeTray tray = await ConnectTrayAsync();
            await tray.SendAsync("ACTIVE_APP:chrome.exe");
            Assert.True(await FakeTray.WaitUntilAsync(() => ActiveWindow() == "chrome"));

            // Eski tepsinin pencere başlığı yok sayılır; sıradaki süreç adı işlenince başlık hâlâ yoktur
            await tray.SendAsync("ACTIVE_WINDOW:Gizli belge - Word");
            await tray.SendAsync("ACTIVE_APP:WINWORD.EXE");
            Assert.True(await FakeTray.WaitUntilAsync(() => ActiveWindow() == "WINWORD"));

            tray.Dispose();
            Assert.True(await FakeTray.WaitUntilAsync(() => ActiveWindow() == "-"));
        }

        // Aydınlatma metni kullanıcı onaylayana kadar her politika eşitlemesinde gösterilir
        [Fact]
        public async Task FairUseText_IsShownUntilTheUserAcknowledges()
        {
            FakeTray tray = await ConnectTrayAsync();
            const string policy = "{\"fair_use_text\":\"Bu bilgisayar izlenir.\",\"dns_domains\":{}}";
            string show = "SHOW_FAIR_USE:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("Bu bilgisayar izlenir."));
            _worker.ApplyPolicy(policy);
            Assert.True(await ReceivedAsync(tray, show));

            await tray.SendAsync("FAIR_USE_ACK");
            await tray.SendAsync("STOP_VISION_TUNNEL");   // sıradaki mesaj: onay işlendi
            Assert.True(await ReceivedAsync(tray, "STOP_CAPTURE"));
            _worker.ApplyPolicy(policy);
            await tray.SendAsync("STOP_VISION_TUNNEL");
            Assert.True(await FakeTray.WaitUntilAsync(() => tray.Received.Count(m => m == "STOP_CAPTURE") == 2));
            Assert.Equal(1, tray.Received.Count(m => m == show));
        }

        // Yardım masası modülü kapanıp açılınca tepsinin menüsü eşitlenir
        [Fact]
        public async Task HelpdeskModuleChange_SyncsTheTrayMenu()
        {
            FakeTray tray = await ConnectTrayAsync();
            Assert.True(await ReceivedAsync(tray, "HELPDESK_MENU:1"));
            _worker.ApplyPolicy("{\"fair_use_text\":\"\",\"dns_domains\":{},\"modules\":{\"helpdesk\":false}}");
            Assert.True(await ReceivedAsync(tray, "HELPDESK_MENU:0"));
            _worker.ApplyPolicy("{\"fair_use_text\":\"\",\"dns_domains\":{},\"modules\":{\"helpdesk\":true}}");
            Assert.True(await FakeTray.WaitUntilAsync(() => tray.Received.Count(m => m == "HELPDESK_MENU:1") == 2));
        }
    }
}
