// =============================================================================
// FirstSyncListReleaseGates
//
// Early branches of RequestFirstSyncListReleaseAfterNames. CAS / Task.Run stay
// on the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum FirstSyncListReleaseAction
    {
        /// <summary>Hold already idle — publish IsCompleted immediately.</summary>
        CompleteNow,

        /// <summary>Another unlock pass is already running.</summary>
        SkipAlreadyInFlight,

        /// <summary>Publish IsCompleted now, then resolve names in the background.</summary>
        UnlockThenScheduleNames
    }

    public static class FirstSyncListReleaseGates
    {
        public static FirstSyncListReleaseAction Decide(
            bool scheduleNamesAfterUnlock,
            bool unlockAlreadyInFlight)
        {
            if (!scheduleNamesAfterUnlock)
            {
                return FirstSyncListReleaseAction.CompleteNow;
            }

            if (unlockAlreadyInFlight)
            {
                return FirstSyncListReleaseAction.SkipAlreadyInFlight;
            }

            return FirstSyncListReleaseAction.UnlockThenScheduleNames;
        }
    }
}
