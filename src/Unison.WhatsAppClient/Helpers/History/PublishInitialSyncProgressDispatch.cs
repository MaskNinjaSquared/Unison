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

            host.WriteInitialSyncProgressJournal(active, completed, processed, total, stage);

            if (active && !completed && !host.FirstSyncListAlreadyReleased)
            {
                host.FirstSyncListHoldOwned = true;
            }

            if (completed)
            {
                host.FirstSyncListAlreadyReleased = true;
                host.FirstSyncListHoldOwned = false;
                host.FirstSyncListReleaseDeferred = false;
            }

            host.PublishInitialSyncChannel(active, completed, processed, total, stage);

            if (completed)
            {
                host.FireAndForgetFlushDiagnosticsOnComplete();
            }
        }
    }
}
