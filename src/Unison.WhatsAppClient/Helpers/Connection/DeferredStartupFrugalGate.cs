// =============================================================================
// DeferredStartupFrugalGate
//
// Frugal-budget skip before deferred startup name/list repair. Visibility and
// memory waits stay on the host.
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum DeferredStartupFrugalAction
    {
        Continue,
        SkipVisibility,
        CheckMemory
    }

    public static class DeferredStartupFrugalGate
    {
        public static DeferredStartupFrugalAction Decide(
            bool useFrugalSyncBudget,
            bool windowVisible)
        {
            if (!useFrugalSyncBudget)
            {
                return DeferredStartupFrugalAction.Continue;
            }

            if (!windowVisible)
            {
                return DeferredStartupFrugalAction.SkipVisibility;
            }

            return DeferredStartupFrugalAction.CheckMemory;
        }

        public static bool ShouldResolveMissingNames(
            int chatCount,
            bool useFrugalSyncBudget,
            bool deferHeavyLocalWork) =>
            chatCount > 0 &&
            !useFrugalSyncBudget &&
            !deferHeavyLocalWork;
    }
}
