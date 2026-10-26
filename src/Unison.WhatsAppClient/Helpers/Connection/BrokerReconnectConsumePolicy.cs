// =============================================================================
// BrokerReconnectConsumePolicy
//
// Whether ConsumeBrokerReconnectRequestAsync should tear down and reconnect.
// IPC load / dispose stay on the host.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class BrokerReconnectConsumePolicy
    {
        /// <summary>
        /// Broker ownership older than this without a live socket entry counts as missing.
        /// </summary>
        public static readonly TimeSpan StaleBrokerGrace = TimeSpan.FromSeconds(3);

        public static bool ShouldConsume(
            bool reconnectRequired,
            bool ownerIsBroker,
            DateTime updatedUtc,
            DateTime utcNow,
            bool brokerSocketStillRegistered)
        {
            if (reconnectRequired)
            {
                return true;
            }

            bool brokerEntryMissing =
                ownerIsBroker &&
                utcNow - updatedUtc > StaleBrokerGrace &&
                !brokerSocketStillRegistered;

            return brokerEntryMissing;
        }
    }
}
