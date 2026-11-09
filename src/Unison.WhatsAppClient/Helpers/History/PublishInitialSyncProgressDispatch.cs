// =============================================================================
// PublishInitialSyncProgressDispatch
//
// Body of PublishInitialSyncProgress. Channel publish / flush stay on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PublishInitialSyncProgressDispatch
    {
        public static void Run(
            IPublishInitialSyncProgressHost host,
            bool active,
            bool completed,
            int processed,
            int total,
            string stage)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            // Trailing history after list unlock must not re-arm IsInitialSyncSafeMode /
            // batch loops (empty StatusV3/PushName, then Recent/NonBlocking).
            bool channelActive = active;
            if (host.FirstSyncListAlreadyReleased && active && !completed)
            {
                channelActive = false;
                stage = (stage ?? string.Empty) + ";clamped=post-release";
            }

            host.WriteInitialSyncProgressJournal(channelActive, completed, processed, total, stage);

            if (channelActive && !completed && !host.FirstSyncListAlreadyReleased)
            {
                host.FirstSyncListHoldOwned = true;
            }

            if (completed)
            {
                host.FirstSyncListAlreadyReleased = true;
                host.FirstSyncListHoldOwned = false;
                host.FirstSyncListReleaseDeferred = false;
            }

            host.PublishInitialSyncChannel(channelActive, completed, processed, total, stage);

            if (completed)
            {
                host.FireAndForgetFlushDiagnosticsOnComplete();
            }
        }
    }
}
