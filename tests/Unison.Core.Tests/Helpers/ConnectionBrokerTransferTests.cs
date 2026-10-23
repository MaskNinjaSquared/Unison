using System;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionBrokerTransferTests
    {
        [Fact]
        public async Task Skips_when_no_ready_socket()
        {
            var host = new FakeHost { Ready = false };
            Assert.False(await ConnectionBrokerTransfer.RunAsync(host, "suspend"));
            Assert.Equal("transfer-skipped", host.LastJournal);
            Assert.Equal(0, host.Transfers);
        }

        [Fact]
        public async Task Returns_true_when_already_broker_owned()
        {
            var host = new FakeHost { Ready = true, AlreadyOwned = true };
            Assert.True(await ConnectionBrokerTransfer.RunAsync(host, "suspend"));
            Assert.Equal(0, host.Transfers);
            Assert.Equal(0, host.HealthStops);
        }

        [Fact]
        public async Task Returns_true_and_publishes_when_transfer_succeeds()
        {
            var host = new FakeHost { Ready = true, TransferSucceeds = true };
            Assert.True(await ConnectionBrokerTransfer.RunAsync(host, "leaving"));
            Assert.Equal(1, host.Transfers);
            Assert.Equal(1, host.BackgroundPublishes);
            Assert.Equal(1, host.HealthStops);
            Assert.Equal(0, host.HealthRestarts);
            Assert.Equal("service-transfer-complete", host.LastJournal);
        }

        [Fact]
        public async Task Restarts_health_when_transfer_fails()
        {
            var host = new FakeHost { Ready = true, TransferSucceeds = false };
            Assert.False(await ConnectionBrokerTransfer.RunAsync(host, "leaving"));
            Assert.Equal(1, host.HealthRestarts);
            Assert.Equal(0, host.BackgroundPublishes);
        }

        [Fact]
        public async Task Restarts_health_when_transfer_throws()
        {
            var host = new FakeHost { Ready = true, TransferThrows = true };
            Assert.False(await ConnectionBrokerTransfer.RunAsync(host, "leaving"));
            Assert.Equal(1, host.HealthRestarts);
            Assert.Equal("service-transfer-failed", host.LastExceptionEvent);
        }

        private sealed class FakeHost : IConnectionBrokerTransferHost
        {
            public bool Ready { get; set; }
            public bool AlreadyOwned { get; set; }
            public bool TransferSucceeds { get; set; }
            public bool TransferThrows { get; set; }
            public int Transfers { get; private set; }
            public int HealthStops { get; private set; }
            public int HealthRestarts { get; private set; }
            public int BackgroundPublishes { get; private set; }
            public string LastJournal { get; private set; } = string.Empty;
            public string LastExceptionEvent { get; private set; } = string.Empty;

            public bool HasReadyForegroundSocket => Ready;
            public bool IsAlreadyBrokerOwned => AlreadyOwned;

            public void StopHealthMonitor(string reason) => HealthStops++;

            public void RestartHealthMonitor() => HealthRestarts++;

            public Task PersistBackgroundDisplayNamesAsync() => Task.CompletedTask;

            public Task PrepareForSuspendAsync() => Task.CompletedTask;

            public Task<bool> TransferSocketToBrokerAsync(string reason)
            {
                Transfers++;
                if (TransferThrows)
                {
                    throw new InvalidOperationException("boom");
                }

                return Task.FromResult(TransferSucceeds);
            }

            public void PublishBackgroundConnection() => BackgroundPublishes++;

            public void WriteBrokerJournal(string eventName, string detail) =>
                LastJournal = eventName;

            public void RecordBrokerException(string eventName, Exception error) =>
                LastExceptionEvent = eventName;
        }
    }
}
