using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistorySyncWireApplyTests
    {
        [Theory]
        [InlineData(true, true, true)]
        [InlineData(false, false, true)]
        [InlineData(true, false, true)]
        [InlineData(false, true, false)]
        public void ShouldDrop_when_fatal_or_no_auth(
            bool fatal,
            bool hasAuth,
            bool expectDrop)
        {
            Assert.Equal(expectDrop, HistorySyncWireApply.ShouldDrop(fatal, hasAuth));
        }

        [Fact]
        public void Run_drops_when_no_auth()
        {
            var host = new FakeHost { HasAuthState = false };
            HistorySyncWireApply.Run(host, new HistorySync());
            Assert.Empty(host.Log);
        }

        [Fact]
        public void Run_applies_and_writes_begin_end()
        {
            var host = new FakeHost { HasAuthState = true };
            var sync = new HistorySync();

            HistorySyncWireApply.Run(host, sync);

            Assert.True(
                host.ApplyFinished.Wait(TimeSpan.FromSeconds(5)),
                "apply should finish on the worker");
            Assert.Equal(2, host.Log.Count);
            Assert.StartsWith("begin:", host.Log[0]);
            Assert.StartsWith("end:", host.Log[1]);
            Assert.Equal(1, host.ApplyCount);
        }

        [Fact]
        public void Run_returns_before_slow_apply_finishes()
        {
            var releaseApply = new ManualResetEventSlim(false);
            var applyEntered = new ManualResetEventSlim(false);
            var host = new FakeHost
            {
                HasAuthState = true,
                ApplyHook = () =>
                {
                    applyEntered.Set();
                    Assert.True(releaseApply.Wait(TimeSpan.FromSeconds(5)));
                    return Task.CompletedTask;
                }
            };

            HistorySyncWireApply.Run(host, new HistorySync());

            Assert.True(
                applyEntered.Wait(TimeSpan.FromSeconds(5)),
                "worker should enter apply without the caller blocking on it");
            Assert.DoesNotContain(host.Log, line => line.StartsWith("end:", StringComparison.Ordinal));

            releaseApply.Set();
            Assert.True(host.ApplyFinished.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(1, host.ApplyCount);
            Assert.Contains(host.Log, line => line.StartsWith("end:", StringComparison.Ordinal));
        }

        private sealed class FakeHost : IHistorySyncWireHost
        {
            private readonly object _logGate = new object();

            public List<string> Log { get; } = new List<string>();

            public int ApplyCount { get; private set; }

            public ManualResetEventSlim ApplyFinished { get; } = new ManualResetEventSlim(false);

            public Func<Task> ApplyHook { get; set; }

            public bool FatalSessionEnded { get; set; }

            public bool HasAuthState { get; set; }

            public void WriteSyncApplyBegin(int conversations, string syncType)
            {
                lock (_logGate)
                {
                    Log.Add("begin:" + conversations + ":" + syncType);
                }
            }

            public void WriteSyncApplyEnd(int conversations, long elapsedMs)
            {
                lock (_logGate)
                {
                    Log.Add("end:" + conversations + ":" + elapsedMs);
                }

                ApplyFinished.Set();
            }

            public Task ApplyHistorySyncAsync(HistorySync sync)
            {
                ApplyCount++;
                if (ApplyHook != null)
                {
                    return ApplyHook();
                }

                return Task.CompletedTask;
            }
        }
    }
}
