using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ConversationResyncWaitDispatchTests
    {
        [Fact]
        public async Task Request_failure_completes_without_wait()
        {
            var host = new FakeHost { RequestOk = false };
            await ConversationResyncWaitDispatch.RunAsync(host);

            Assert.True(host.FailedCompleted);
            Assert.False(host.WaitCleared);
            Assert.Contains("request failed", host.LastLog);
        }

        [Fact]
        public async Task Request_success_waits_then_clears()
        {
            var host = new FakeHost { RequestOk = true, CompleteImmediately = true };
            await ConversationResyncWaitDispatch.RunAsync(host);

            Assert.True(host.WaitCleared);
            Assert.Contains("history observed", host.LastLog);
        }

        private sealed class FakeHost : IConversationResyncWaitHost
        {
            public bool RequestOk;
            public bool CompleteImmediately;
            public bool FailedCompleted;
            public bool WaitCleared;
            public string LastLog;
            private TaskCompletionSource<bool> _armed;

            public TimeSpan HistoryWaitTimeout => TimeSpan.FromMilliseconds(50);

            public void LogConversationResync(string message) => LastLog = message;

            public void RaiseConversationResyncStatus(string status)
            {
            }

            public void ArmUserResyncHistoryWait(TaskCompletionSource<bool> historyDone)
            {
                _armed = historyDone;
                if (CompleteImmediately)
                {
                    historyDone.TrySetResult(true);
                }
            }

            public Task<bool> EnsureFullHistoryRequestedForUserResyncAsync(string reason) =>
                Task.FromResult(RequestOk);

            public void CompleteUserResyncHistoryWaitFailed(TaskCompletionSource<bool> historyDone)
            {
                FailedCompleted = true;
                historyDone.TrySetResult(false);
            }

            public void ClearUserResyncHistoryWait(TaskCompletionSource<bool> historyDone) =>
                WaitCleared = true;
        }
    }
}
