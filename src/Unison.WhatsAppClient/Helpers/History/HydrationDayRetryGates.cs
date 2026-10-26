// =============================================================================
// HydrationDayRetryGates
//
// Whether ScheduleHydrationDayRetryIfNeeded should arm the day timer.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public static class HydrationDayRetryGates
    {
        public static bool ShouldArm(bool stillStale, int dayAttempts, int maxDayAttempts) =>
            stillStale && dayAttempts < maxDayAttempts;
    }
}
