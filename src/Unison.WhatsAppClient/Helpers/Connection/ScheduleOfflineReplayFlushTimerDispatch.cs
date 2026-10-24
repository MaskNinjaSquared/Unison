// =============================================================================
// ScheduleOfflineReplayFlushTimerDispatch
//
// Body of ScheduleOfflineReplayFlushTimer. Timer field swap stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ScheduleOfflineReplayFlushTimerDispatch
    {
        public static void Run(IScheduleOfflineReplayFlushTimerHost host, Action onIdle)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (onIdle == null)
            {
                throw new ArgumentNullException(nameof(onIdle));
            }

            host.ReplaceOfflineReplayFlushTimer(onIdle, host.OfflineReplayFlushDueMs);
        }
    }
}
