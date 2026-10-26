using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BrokerReconnectConsumeDispatchTests
    {
        [Fact]
        public async Task Lock_timeout_returns_false()
        {
            var host = new FakeHost { LeaseOk = false };
            Assert.False(await BrokerReconnectConsumeDispatch.RunAsync(host));
            Assert.True(host.LockTimeoutWritten);
            Assert.False(host.TornDown);
        }

        [Fact]
        public async Task Missing_state_returns_false()
        {
            var host = new FakeHost { LeaseOk = true, Snapshot = null };
            Assert.False(await BrokerReconnectConsumeDispatch.RunAsync(host));
            Assert.True(host.LeaseReleased);
            Assert.False(host.TornDown);
        }

        [Fact]
        public async Task Policy_skip_does_not_tear_down()
        {
            var host = new FakeHost
            {
                LeaseOk = true,
                Snapshot = new BrokerReconnectOwnershipSnapshot
                {
                    ReconnectRequired = false,
                    OwnerIsBroker = false,
                    UpdatedUtc = DateTime.UtcNow,
                    BrokerSocketStillRegistered = true
                }
            };
            Assert.False(await BrokerReconnectConsumeDispatch.RunAsync(host));
            Assert.False(host.TornDown);
            Assert.True(host.LeaseReleased);
        }

        [Fact]
        public async Task Reconnect_required_tears_down()
        {
            var host = new FakeHost
            {
                LeaseOk = true,
                Snapshot = new BrokerReconnectOwnershipSnapshot
                {
                    ReconnectRequired = true,
                    OwnerIsBroker = true,
                    UpdatedUtc = DateTime.UtcNow,
                    BrokerSocketStillRegistered = true,
                    SocketId = "s1"
                }
            };
            Assert.True(await BrokerReconnectConsumeDispatch.RunAsync(host));
            Assert.True(host.TornDown);
            Assert.True(host.ConsumedWritten);
            Assert.True(host.LeaseReleased);
        }

        private sealed class FakeHost : IBrokerReconnectConsumeHost
        {
            public bool LeaseOk;
            public BrokerReconnectOwnershipSnapshot Snapshot;
            public bool LockTimeoutWritten;
            public bool LeaseReleased;
            public bool TornDown;
            public bool ConsumedWritten;

            public Task<bool> TryAcquireBrokerReconnectLeaseAsync() =>
                Task.FromResult(LeaseOk);

            public Task ReleaseBrokerReconnectLeaseAsync()
            {
                LeaseReleased = true;
                return Task.CompletedTask;
            }

            public void WriteBrokerReconnectLockTimeout() => LockTimeoutWritten = true;

            public Task<BrokerReconnectOwnershipSnapshot> LoadBrokerOwnershipSnapshotAsync() =>
                Task.FromResult(Snapshot);

            public void WriteBrokerReconnectConsumed(
                BrokerReconnectOwnershipSnapshot snapshot,
                bool brokerEntryMissing) =>
                ConsumedWritten = true;

            public Task TearDownAfterBrokerReconnectConsumeAsync(
                BrokerReconnectOwnershipSnapshot snapshot)
            {
                TornDown = true;
                return Task.CompletedTask;
            }

            public void RecordBrokerReconnectConsumeFailed(Exception error)
            {
            }
        }
    }
}
