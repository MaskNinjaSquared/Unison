using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionLifecycleTests
    {
        [Fact]
        public async Task Prepare_flushes_journal_and_journals_success()
        {
            var host = new FakeHost();
            await ConnectionLifecycle.PrepareForSuspendAsync(host);
            Assert.Equal(1, host.Flushes);
            Assert.Equal("suspend-incoming-journal-flushed", host.LastJournal);
        }

        [Fact]
        public async Task Shutdown_disconnects_and_skips_persist_when_asked()
        {
            var host = new FakeHost();
            await ConnectionLifecycle.ShutdownAsync(host, persist: false);
            Assert.True(host.Latched);
            Assert.Equal(1, host.Disconnects);
            Assert.Equal(0, host.Persists);
        }

        [Fact]
        public async Task Shutdown_persists_tail_when_requested()
        {
            var host = new FakeHost();
            await ConnectionLifecycle.ShutdownAsync(host, persist: true);
            Assert.Equal(1, host.Persists);
        }

        private sealed class FakeHost : IConnectionLifecycleHost
        {
            public int Flushes { get; private set; }
            public int Disconnects { get; private set; }
            public int Persists { get; private set; }
            public bool Latched { get; private set; }
            public string LastJournal { get; private set; } = string.Empty;

            public Task FlushIncomingJournalForSuspendAsync()
            {
                Flushes++;
                return Task.CompletedTask;
            }

            public void WriteLifecycleJournal(string eventName, string detail = null) =>
                LastJournal = eventName;

            public void RecordLifecycleException(string eventName, Exception error)
            {
            }

            public void BeginShutdownLatch() => Latched = true;

            public void StopHealthMonitor(string reason)
            {
            }

            public void CancelBackgroundWorkForShutdown()
            {
            }

            public void ResetPersistScheduler()
            {
            }

            public Task WaitForIncomingQueueDrainAsync(int timeoutMs) => Task.CompletedTask;

            public void DisconnectAndDisposeSocket() => Disconnects++;

            public void PublishSuspended()
            {
            }

            public void ResetIncomingPumpForSuspend()
            {
            }

            public Task PersistSuspendTailWithBudgetAsync(int budgetMs)
            {
                Persists++;
                return Task.CompletedTask;
            }
        }
    }
}
