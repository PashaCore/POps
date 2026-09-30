using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // Arka plan raporlayıcılarının işletim sistemi ve ağ sınırları sahtedir; yalnız karar ve durum akışı denenir.
    public class ReporterFlowTests : TestBase, IDisposable
    {
        private readonly string _data = TestEnvironment.NewDir("reporter-flow");

        public ReporterFlowTests()
        {
            AgentUpdate.DataDir = _data;
            SecureStore.Dir = TestEnvironment.NewDir("reporter-secret");
            AgentCredentials.SaveSecret("reporter-secret-0123456789abcdefghijkl", "HW-R");
        }

        public void Dispose() => AgentUpdate.DataDir = TestEnvironment.DefaultDataDir;

        private static List<SoftwareItem> Inventory(string version = "1") => new List<SoftwareItem>
        {
            new SoftwareItem { Name = "POps Test", Version = version },
        };

        [Fact]
        public async Task SoftwareReport_SuccessIsRememberedAndInvokesHealthCallback()
        {
            int posts = 0, uploaded = 0;
            DateTime now = new DateTime(2026, 9, 30, 10, 0, 0, DateTimeKind.Utc);
            var reporter = new SoftwareReporter("https://pops.example", () => "HW-R", () => uploaded++)
            {
                Collector = () => Inventory(),
                UtcNow = () => now,
                Poster = (id, payload) =>
                {
                    Assert.Equal("HW-R", id);
                    Assert.Single(payload.Items);
                    posts++;
                    return Task.FromResult(PostResult.Sent);
                },
            };

            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Equal((1, 1), (posts, uploaded));

            now = now.AddDays(1);
            Assert.Null(await reporter.ReportOnceAsync());
            Assert.Equal((2, 2), (posts, uploaded));
        }

        [Theory]
        [InlineData(PostResult.Failed, 15)]
        [InlineData(PostResult.EndpointMissing, 1440)]
        public async Task SoftwareReport_FailureUsesTheExpectedBackoff(PostResult result, int minutes)
        {
            var reporter = new SoftwareReporter("https://pops.example", () => "HW-R")
            {
                Collector = () => Inventory("2"),
                Poster = (_, _) => Task.FromResult(result),
            };
            Assert.Equal(TimeSpan.FromMinutes(minutes), await reporter.ReportOnceAsync());
        }

        [Fact]
        public async Task SoftwareReport_CollectorFailureIsReportedWithoutStoppingTheLoop()
        {
            string error = null;
            var reporter = new SoftwareReporter("https://pops.example", () => "HW-R", error: value => error = value)
            {
                Collector = () => throw new InvalidOperationException("registry unavailable"),
            };
            Assert.Equal(SoftwareReporter.RetryDelay, await reporter.ReportOnceAsync());
            Assert.Equal("registry unavailable", error);
        }

        private static SessionSnapshot Session(string user, int id) => new SessionSnapshot
        {
            User = user,
            SessionId = id,
            BootUtc = new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc),
        };

        [Fact]
        public async Task SessionSwitch_ReportsLogoutThenLoginAndPersistsTheNewState()
        {
            var calls = new List<(string Action, string User)>();
            var reporter = new SessionReporter("https://pops.example", () => "HW-R", "PC-R")
            {
                Poster = (action, id, body) =>
                {
                    Assert.Equal(("HW-R", "PC-R"), (id, body.Hostname));
                    calls.Add((action, body.StudentId));
                    return Task.FromResult(true);
                },
            };

            SessionSnapshot current = Session("ayse", 2);
            (SessionSnapshot saved, bool delivered) = await reporter.ReportChangesAsync(Session("ali", 1), current);
            Assert.True(delivered);
            Assert.Same(current, saved);
            Assert.Equal(new[] { ("logout", "ali"), ("login", "ayse") }, calls);
            Assert.Equal("ayse", SessionReporter.Load().User);
        }

        [Fact]
        public async Task SessionFailure_KeepsTheLastReportedState()
        {
            SessionSnapshot previous = Session("ali", 1);
            var reporter = new SessionReporter("https://pops.example", () => "HW-R", "PC-R")
            {
                Poster = (_, _, _) => Task.FromResult(false),
            };
            (SessionSnapshot saved, bool delivered) = await reporter.ReportChangesAsync(previous, Session("ayse", 2));
            Assert.False(delivered);
            Assert.Same(previous, saved);
        }
    }

    public class AgentHttpFlowTests : TestBase, IDisposable
    {
        private sealed class Handler : HttpMessageHandler
        {
            public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; }
            public HttpRequestMessage Seen { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Seen = request;
                return Task.FromResult(Reply(request));
            }
        }

        private readonly HttpClient _previous = AgentHttp.Client;
        private readonly Handler _handler = new Handler();

        public AgentHttpFlowTests()
        {
            SecureStore.Dir = TestEnvironment.NewDir("http-flow");
            AgentCredentials.SaveSecret("http-secret-0123456789abcdefghijklmn", "HW-H");
            AgentHttp.Client = new HttpClient(_handler);
        }

        public void Dispose()
        {
            AgentHttp.Client.Dispose();
            AgentHttp.Client = _previous;
        }

        [Fact]
        public async Task SendAsync_AddsDeviceHeadersAndReturnsTheBoundedBody()
        {
            _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
            var result = await AgentHttp.SendAsync(HttpMethod.Post, "https://pops.example", "/api/test", "HW-H", new { value = 7 }, "test");

            Assert.Equal(200, result.Status);
            Assert.Equal("{\"ok\":true}", result.Body);
            Assert.Equal("HW-H", _handler.Seen.Headers.GetValues("X-Agent-Id").Single());
            Assert.NotEmpty(_handler.Seen.Headers.GetValues("X-Agent-Secret").Single());
        }

        [Fact]
        public async Task SendAsync_OversizedAndNetworkFailureReturnSafeResults()
        {
            int previousLimit = AgentHttp.MaxResponseBytes;
            try
            {
                AgentHttp.MaxResponseBytes = 3;
                _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                {
                    Content = new StringContent("large"),
                };
                var oversized = await AgentHttp.SendAsync(HttpMethod.Get, "https://pops.example", "/api/test", "HW-H", null, "test");
                Assert.Equal(429, oversized.Status);
                Assert.Null(oversized.Body);

                _handler.Reply = _ => throw new HttpRequestException("offline");
                var failed = await AgentHttp.SendAsync(HttpMethod.Get, "https://pops.example", "/api/test", "HW-H", null, "test");
                Assert.Null(failed.Status);
                Assert.Null(failed.Body);
            }
            finally { AgentHttp.MaxResponseBytes = previousLimit; }
        }

        [Fact]
        public async Task PostJsonAsync_ReturnsTrueOnlyForSuccess()
        {
            _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.NoContent);
            Assert.True(await AgentHttp.PostJsonAsync("https://pops.example", "/api/test", "HW-H", new { }, "test"));
            _handler.Reply = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
            Assert.False(await AgentHttp.PostJsonAsync("https://pops.example", "/api/test", "HW-H", new { }, "test"));
        }
    }

    public class CommandResultTests : TestBase
    {
        [Fact]
        public void ResultKeepsExitAndDurationButBoundsOutput()
        {
            var result = new CommandExecutionResult(new string('x', CommandExecutionPolicy.MaxOutputChars + 50), 7, TimeSpan.FromSeconds(3));
            Assert.Equal(CommandExecutionPolicy.MaxOutputChars, result.Output.Length);
            Assert.Equal(7, result.ExitCode);
            Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);
        }
    }

    public sealed class FakeUpdateCollection
    {
        public List<object> Values { get; } = new List<object>();
        public int Count => Values.Count;
        public object Item(int index) => Values[index];
    }

    public sealed class FakeUpdateCategory
    {
        public string Name { get; set; }
        public string CategoryID { get; set; }
    }

    public sealed class FakeInstallBehavior
    {
        public bool CanRequestUserInput { get; set; }
    }

    public sealed class FakeUpdate
    {
        public string Title { get; set; }
        public string MsrcSeverity { get; set; }
        public FakeUpdateCollection KBArticleIDs { get; } = new FakeUpdateCollection();
        public FakeUpdateCollection Categories { get; } = new FakeUpdateCollection();
        public FakeInstallBehavior InstallationBehavior { get; } = new FakeInstallBehavior();
        public bool BrowseOnly { get; set; }
    }

    public sealed class FakeSearchResult
    {
        public int ResultCode { get; set; } = 2;
        public FakeUpdateCollection Updates { get; } = new FakeUpdateCollection();
    }

    public sealed class FakeHistoryEntry
    {
        public int Operation { get; set; }
        public int ResultCode { get; set; }
        public DateTime Date { get; set; }
    }

    public sealed class FakeHistoryCollection
    {
        public List<FakeHistoryEntry> Values { get; } = new List<FakeHistoryEntry>();
        public int Count => Values.Count;
        public FakeHistoryEntry Item(int index) => Values[index];
    }

    public sealed class FakeUpdateSearcher
    {
        public FakeWuaJob Job { get; } = new FakeWuaJob { IsCompleted = true };
        public FakeSearchResult Result { get; } = new FakeSearchResult();
        public FakeHistoryCollection History { get; } = new FakeHistoryCollection();
        public string CriteriaSeen { get; private set; }
        public object BeginSearch(string criteria, object callback, object state) { CriteriaSeen = criteria; return Job; }
        public FakeSearchResult EndSearch(object job) => Result;
        public int GetTotalHistoryCount() => History.Count;
        public FakeHistoryCollection QueryHistory(int start, int count) => History;
    }

    public sealed class FakeUpdateSession
    {
        public FakeUpdateSearcher Searcher { get; } = new FakeUpdateSearcher();
        public FakeUpdateSearcher CreateUpdateSearcher() => Searcher;
    }

    public class WindowsUpdateFlowTests : TestBase
    {
        [Fact]
        public void Search_MapsTheComShapedResultAndCleansTheJob()
        {
            var session = new FakeUpdateSession();
            var update = new FakeUpdate { Title = "Güvenlik düzeltmesi", MsrcSeverity = "Critical" };
            update.KBArticleIDs.Values.Add("123456");
            update.Categories.Values.Add(new FakeUpdateCategory { Name = "Security Updates", CategoryID = "cat-1" });
            session.Searcher.Result.Updates.Values.Add(update);

            WindowsUpdateAgent.Scan scan = WindowsUpdateAgent.Search(session, TimeSpan.FromSeconds(1));

            Assert.Equal(WindowsUpdateAgent.Criteria, session.Searcher.CriteriaSeen);
            Assert.Equal(1, session.Searcher.Job.CleanUps);
            PendingUpdate pending = Assert.Single(scan.Pending);
            Assert.Equal(("KB123456", "Güvenlik düzeltmesi", "Critical"), (pending.Kb, pending.Title, pending.Severity));
            Assert.Equal("Security Updates", Assert.Single(pending.Categories));
            Assert.Equal("cat-1", Assert.Single(pending.CategoryIds));
            Assert.False(pending.BrowseOnly);
        }

        [Fact]
        public void Search_RejectsFailedAndAbortedResultsButStillCleansUp()
        {
            foreach (int code in new[] { 5, 4 })
            {
                var session = new FakeUpdateSession();
                session.Searcher.Result.ResultCode = code;
                Assert.ThrowsAny<Exception>(() => WindowsUpdateAgent.Search(session, TimeSpan.FromSeconds(1)));
                Assert.Equal(1, session.Searcher.Job.CleanUps);
            }
        }

        [Fact]
        public void History_ReturnsTheLatestSuccessfulInstallation()
        {
            var session = new FakeUpdateSession();
            session.Searcher.History.Values.AddRange(new[]
            {
                new FakeHistoryEntry { Operation = 1, ResultCode = 2, Date = new DateTime(2026, 9, 1) },
                new FakeHistoryEntry { Operation = 1, ResultCode = 4, Date = new DateTime(2026, 9, 20) },
                new FakeHistoryEntry { Operation = 2, ResultCode = 2, Date = new DateTime(2026, 9, 25) },
                new FakeHistoryEntry { Operation = 1, ResultCode = 2, Date = new DateTime(2026, 9, 15) },
            });
            Assert.Equal(new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc), WindowsUpdateAgent.LastSuccessfulInstallUtc(session));

            session.Searcher.History.Values.Clear();
            Assert.Null(WindowsUpdateAgent.LastSuccessfulInstallUtc(session));
        }

        [Fact]
        public void Install_SkipsUpdatesThatNeedUserInputWithoutCallingCom()
        {
            var scan = new WindowsUpdateAgent.Scan();
            scan.Pending.Add(new PendingUpdate { Title = "Managed", Severity = "Critical", NeedsUserInput = true });
            scan.Handles.Add(new object());
            WindowsUpdateAgent.InstallOutcome outcome = WindowsUpdateAgent.Install(new object(), scan, new[] { 0 }, TimeSpan.FromSeconds(1));
            Assert.Equal(1, outcome.Skipped);
            Assert.Empty(outcome.Failed);
            Assert.Equal(0, outcome.Installed);
        }

        [Fact]
        public void ErrorText_IncludesComHResult()
        {
            string text = WindowsUpdateAgent.ErrorText(new COMException("busy", unchecked((int)0x80240016)));
            Assert.Contains("0x80240016", text);
            Assert.EndsWith("busy", text);
            Assert.Equal("plain", WindowsUpdateAgent.ErrorText(new InvalidOperationException("plain")));
        }
    }
}
