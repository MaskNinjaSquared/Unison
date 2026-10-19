// =============================================================================
// IncomingPumpStall
//
// The pump is stalled when it is running, it has work, and the current stage
// has sat longer than the limit. A stopped pump or an empty queue is not a stall.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class IncomingPumpStall
    {
        public static bool IsStalled(
            bool running,
            long stageUtcTicks,
            bool hasWork,
            DateTime utcNow,
            TimeSpan limit)
        {
            if (!running || stageUtcTicks <= 0 || !hasWork)
            {
                return false;
            }

            var stageUtc = new DateTime(stageUtcTicks, DateTimeKind.Utc);
            return utcNow - stageUtc > limit;
        }
    }
}
