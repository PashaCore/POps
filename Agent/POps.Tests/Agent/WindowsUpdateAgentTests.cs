using System;
using System.Diagnostics;
using System.Threading;
using POpsAgent;
using Xunit;

namespace POps.Tests.Agent
{
    // WUA iş nesnesinin sahtesi (dynamic ile çağrılır; gerçek COM yok)
    public sealed class FakeWuaJob
    {
        public bool IsCompleted { get; set; }
        public int Aborts;
        public int CleanUps;
        public int CleanUpThread;
        public readonly ManualResetEventSlim AllowCleanUp = new ManualResetEventSlim(true);
        public void RequestAbort() => Interlocked.Increment(ref Aborts);
        public void CleanUp()
        {
            CleanUpThread = Environment.CurrentManagedThreadId;
            AllowCleanUp.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Increment(ref CleanUps);
        }
    }

    public sealed class ThrowingWuaJob
    {
        public int CleanUps;
        public bool IsCompleted => throw new InvalidOperationException("RPC sunucusu yok");
        public void CleanUp() => CleanUps++;
    }

    public class WindowsUpdateAgentTests : TestBase
    {
        [Fact]
        public void HungJob_IsAbortedAndNotWaitedForever()
        {
            TimeSpan previous = WindowsUpdateAgent.AbortGrace;
            WindowsUpdateAgent.AbortGrace = TimeSpan.FromMilliseconds(300);
            try
            {
                var job = new FakeWuaJob { IsCompleted = false };
                var clock = Stopwatch.StartNew();
                Assert.True(WindowsUpdateAgent.WaitForJob(job, TimeSpan.FromMilliseconds(100)));
                Assert.InRange(clock.Elapsed, TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(5));
                Assert.Equal(1, job.Aborts);
            }
            finally { WindowsUpdateAgent.AbortGrace = previous; }
        }

        [Fact]
        public void FinishedJob_IsNotAborted()
        {
            var job = new FakeWuaJob { IsCompleted = true };
            Assert.False(WindowsUpdateAgent.WaitForJob(job, TimeSpan.FromMinutes(1)));
            Assert.Equal(0, job.Aborts);
        }

        // M1: CleanUp işin bitmesini bekler; bitmemiş işte çağıran thread bloklanmamalı
        [Fact]
        public void UnfinishedJob_IsCleanedUpOnADetachedThread()
        {
            var job = new FakeWuaJob { IsCompleted = false };
            job.AllowCleanUp.Reset();   // CleanUp "iş bitene kadar" takılı kalır

            var clock = Stopwatch.StartNew();
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), "ReleaseJob bitmemiş işi beklememeli");

            job.AllowCleanUp.Set();
            SpinWait.SpinUntil(() => Volatile.Read(ref job.CleanUps) == 1, TimeSpan.FromSeconds(5));
            Assert.Equal(1, job.CleanUps);
            Assert.NotEqual(Environment.CurrentManagedThreadId, job.CleanUpThread);
        }

        [Fact]
        public void FinishedJob_IsCleanedUpRightAway()
        {
            var job = new FakeWuaJob { IsCompleted = true };
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.Equal(1, job.CleanUps);
            Assert.Equal(Environment.CurrentManagedThreadId, job.CleanUpThread);
        }

        [Fact]
        public void UnreadableJob_IsLeftAlone()
        {
            var job = new ThrowingWuaJob();
            WindowsUpdateAgent.ReleaseJob(job);
            Assert.Equal(0, job.CleanUps);
        }
    }
}
