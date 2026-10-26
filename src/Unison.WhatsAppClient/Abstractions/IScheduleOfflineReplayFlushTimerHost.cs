// =============================================================================
// IScheduleOfflineReplayFlushTimerHost
//
// Side effects for ScheduleOfflineReplayFlushTimerDispatch.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IScheduleOfflineReplayFlushTimerHost
    {
        int OfflineReplayFlushDueMs { get; }

        void ReplaceOfflineReplayFlushTimer(Action callback, int dueMs);
    }
}
