// =============================================================================
// IBrokerReconnectConsumeHost
//
// Side effects for BrokerReconnectConsumeDispatch. Broker IPC types stay on the
// host; this surface only passes the fields the policy needs.
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public sealed class BrokerReconnectOwnershipSnapshot
    {
        public bool ReconnectRequired { get; set; }

        public bool OwnerIsBroker { get; set; }

        public DateTime UpdatedUtc { get; set; }

        public bool BrokerSocketStillRegistered { get; set; }

        public string SocketId { get; set; }

        public int SocketClosedCount { get; set; }

        public string LastReason { get; set; }
    }

    public interface IBrokerReconnectConsumeHost
    {
        Task<bool> TryAcquireBrokerReconnectLeaseAsync();

        Task ReleaseBrokerReconnectLeaseAsync();

        void WriteBrokerReconnectLockTimeout();

        /// <summary>Null when ownership state is missing.</summary>
        Task<BrokerReconnectOwnershipSnapshot> LoadBrokerOwnershipSnapshotAsync();

        void WriteBrokerReconnectConsumed(
            BrokerReconnectOwnershipSnapshot snapshot,
            bool brokerEntryMissing);

        Task TearDownAfterBrokerReconnectConsumeAsync(BrokerReconnectOwnershipSnapshot snapshot);

        void RecordBrokerReconnectConsumeFailed(Exception error);
    }
}
