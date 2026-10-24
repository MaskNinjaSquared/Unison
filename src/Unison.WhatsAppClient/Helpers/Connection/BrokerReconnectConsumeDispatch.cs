// =============================================================================
// BrokerReconnectConsumeDispatch
//
// Body of ConsumeBrokerReconnectRequestAsync. Lease / ownership store / socket
// dispose stay on the host.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class BrokerReconnectConsumeDispatch
    {
        public static async Task<bool> RunAsync(IBrokerReconnectConsumeHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            bool leaseHeld = false;
            try
            {
                if (!await host.TryAcquireBrokerReconnectLeaseAsync().ConfigureAwait(false))
                {
                    host.WriteBrokerReconnectLockTimeout();
                    return false;
                }

                leaseHeld = true;

                BrokerReconnectOwnershipSnapshot state =
                    await host.LoadBrokerOwnershipSnapshotAsync().ConfigureAwait(false);
                if (state == null)
                {
                    return false;
                }

                DateTime nowUtc = DateTime.UtcNow;
                bool brokerEntryMissing =
                    state.OwnerIsBroker &&
                    nowUtc - state.UpdatedUtc > BrokerReconnectConsumePolicy.StaleBrokerGrace &&
                    !state.BrokerSocketStillRegistered;

                if (!BrokerReconnectConsumePolicy.ShouldConsume(
                        state.ReconnectRequired,
                        state.OwnerIsBroker,
                        state.UpdatedUtc,
                        nowUtc,
                        state.BrokerSocketStillRegistered))
                {
                    return false;
                }

                host.WriteBrokerReconnectConsumed(state, brokerEntryMissing);
                await host.TearDownAfterBrokerReconnectConsumeAsync(state).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                host.RecordBrokerReconnectConsumeFailed(ex);
                return false;
            }
            finally
            {
                if (leaseHeld)
                {
                    await host.ReleaseBrokerReconnectLeaseAsync().ConfigureAwait(false);
                }
            }
        }
    }
}
