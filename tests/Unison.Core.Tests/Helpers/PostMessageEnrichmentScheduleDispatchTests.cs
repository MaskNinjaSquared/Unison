using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PostMessageEnrichmentScheduleDispatchTests
    {
        [Fact]
        public void Already_started_skips_work()
        {
            var host = new FakeHost { ClaimOk = false };
            PostMessageEnrichmentScheduleDispatch.Schedule(host, "t");
            Assert.False(host.FireAndForgetCalled);
            Assert.Contains("already started", host.LastLog);
        }

        [Fact]
        public void Claims_and_fires_background_run()
        {
            var host = new FakeHost { ClaimOk = true };
            PostMessageEnrichmentScheduleDispatch.Schedule(host, "go");
            Assert.True(host.AwaitingCleared);
            Assert.True(host.CtsReplaced);
            Assert.True(host.FireAndForgetCalled);
            Assert.True(host.ScheduledWritten);
        }

        private sealed class FakeHost : IPostMessageEnrichmentScheduleHost
        {
            public bool ClaimOk;
            public bool AwaitingCleared;
            public bool CtsReplaced;
            public bool FireAndForgetCalled;
            public bool ScheduledWritten;
            public string LastLog;

            public bool TryClaimPostMessageEnrichmentStarted() => ClaimOk;

            public void ClearEnrichmentAwaitingHeavyHistory() => AwaitingCleared = true;

            public CancellationTokenSource ReplacePostMessageEnrichmentCts()
            {
                CtsReplaced = true;
                return new CancellationTokenSource();
            }

            public void LogPostMessageEnrichment(string message) => LastLog = message;

            public void WritePostMessageEnrichmentScheduled(string reason) =>
                ScheduledWritten = true;

            public void FireAndForgetPostMessageEnrichment(Func<Task> work) =>
                FireAndForgetCalled = true;

            public Task RunPostMessageEnrichmentAsync(string reason, CancellationToken token) =>
                Task.CompletedTask;

            public void ClearPostMessageEnrichmentCtsIfCurrent(CancellationTokenSource cts)
            {
            }
        }
    }
}
