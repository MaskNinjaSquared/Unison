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

        /// <summary>Resolve names under the hold, then publish IsCompleted.</summary>
        StartNamesThenUnlock
    }

    public static class FirstSyncListReleaseGates
    {
        public static FirstSyncListReleaseAction Decide(
            bool shouldResolveNamesBeforeRelease,
            bool unlockAlreadyInFlight)
        {
            if (!shouldResolveNamesBeforeRelease)
            {
                return FirstSyncListReleaseAction.CompleteNow;
            }

            if (unlockAlreadyInFlight)
            {
                return FirstSyncListReleaseAction.SkipAlreadyInFlight;
            }

            return FirstSyncListReleaseAction.StartNamesThenUnlock;
        }
    }
}
