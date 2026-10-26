using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class RaiseSessionClearedDispatchTests
    {
        [Fact]
        public async Task Resets_notifies_and_raises_ui()
        {
            var host = new FakeHost();
            await RaiseSessionClearedDispatch.RunAsync(host, startPairing: true);
            Assert.True(host.HoldReset);
            Assert.True(host.HistoryNotified);
            Assert.True(host.UiRaised);
            Assert.True(host.UiStartPairing);
        }

        [Fact]
        public async Task History_notify_failure_still_raises_ui()
        {
            var host = new FakeHost { ThrowOnHistory = true };
            await RaiseSessionClearedDispatch.RunAsync(host, startPairing: false);
            Assert.True(host.UiRaised);
            Assert.Contains("NotifySessionCleared failed", host.LastLog);
        }

        private sealed class FakeHost : IRaiseSessionClearedHost
        {
            public bool HoldReset;
            public bool HistoryNotified;
            public bool UiRaised;
            public bool UiStartPairing;
            public bool ThrowOnHistory;
            public string LastLog;

            public void ResetFirstSyncListHoldFlags(string reason) => HoldReset = true;

            public void NotifyHistorySessionCleared()
            {
                if (ThrowOnHistory)
                {
                    throw new InvalidOperationException("boom");
                }

                HistoryNotified = true;
            }

            public void LogSessionCleared(string message) => LastLog = message;

            public Task RaiseOnSessionClearedOnUiAsync(bool startPairing)
            {
                UiRaised = true;
                UiStartPairing = startPairing;
                return Task.CompletedTask;
            }
        }
    }
}
