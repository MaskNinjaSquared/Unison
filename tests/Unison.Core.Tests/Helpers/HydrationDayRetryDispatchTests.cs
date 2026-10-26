using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HydrationDayRetryDispatchTests
    {
        [Fact]
        public void Does_not_arm_when_tips_fresh()
        {
            var host = new FakeHost { StillStale = false, Attempts = 1 };
            HydrationDayRetryDispatch.Schedule(host, "t");
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Does_not_arm_when_day_budget_exhausted()
        {
            var host = new FakeHost { StillStale = true, Attempts = 5, MaxAttempts = 5 };
            HydrationDayRetryDispatch.Schedule(host, "t");
            Assert.False(host.FireAndForgetCalled);
        }

        [Fact]
        public void Arms_when_stale_and_budget_remains()
        {
            var host = new FakeHost { StillStale = true, Attempts = 1, MaxAttempts = 5 };
            HydrationDayRetryDispatch.Schedule(host, "go");
            Assert.True(host.CtsReplaced);
            Assert.True(host.FireAndForgetCalled);
        }

        [Fact]
        public async Task After_delay_runs_pipeline_when_still_stale()
        {
            var host = new FakeHost { StillStale = true };
            var cts = new CancellationTokenSource();
            await HydrationDayRetryDispatch.RunAfterDelayAsync(host, "t", cts);
            Assert.True(host.PipelineRan);
        }

        [Fact]
        public async Task After_delay_skips_when_tips_became_fresh()
        {
            var host = new FakeHost { StillStale = false };
            var cts = new CancellationTokenSource();
            await HydrationDayRetryDispatch.RunAfterDelayAsync(host, "t", cts);
            Assert.False(host.PipelineRan);
        }

        private sealed class FakeHost : IHydrationDayRetryHost
        {
            public bool StillStale;
            public int Attempts;
            public int MaxAttempts = 5;
            public bool CtsReplaced;
            public bool FireAndForgetCalled;
            public bool PipelineRan;

            public int HydrationDayAttempts => Attempts;
            public int MaxHydrationDayAttempts => MaxAttempts;
            public TimeSpan HydrationDayRetryInterval => TimeSpan.Zero;

            public bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason)
            {
                staleReason = StillStale ? "tip-old" : null;
                return StillStale;
            }

            public CancellationTokenSource ReplaceHydrationDayRetryCts()
            {
                CtsReplaced = true;
                return new CancellationTokenSource();
            }

            public void LogHydrationDayRetry(string message)
            {
            }

            public void FireAndForgetHydrationDayRetry(Func<Task> work) =>
                FireAndForgetCalled = true;

            public Task DelayHydrationDayRetryAsync(TimeSpan delay, CancellationToken token) =>
                Task.CompletedTask;

            public Task RunHydrationFallbackPipelineAsync(string reason)
            {
                PipelineRan = true;
                return Task.CompletedTask;
            }
        }
    }
}
