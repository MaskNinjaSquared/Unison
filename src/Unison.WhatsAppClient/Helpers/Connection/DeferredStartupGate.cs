// =============================================================================
// DeferredStartupGate
//
// Visibility / memory gate before deferred startup name and list repair. Which
// requirements apply is a product decision (IStartupMaintenanceTuning).
// =============================================================================

namespace Unison.WhatsAppClient.Helpers
{
    public enum DeferredStartupGateAction
    {
        Continue,
        SkipVisibility,
        CheckMemory
    }

    public static class DeferredStartupGate
    {
        public static DeferredStartupGateAction Decide(
            bool requireVisibleWindow,
            bool requireMemoryHeadroom,
            bool windowVisible)
        {
            if (requireVisibleWindow && !windowVisible)
            {
                return DeferredStartupGateAction.SkipVisibility;
            }

            return requireMemoryHeadroom
                ? DeferredStartupGateAction.CheckMemory
                : DeferredStartupGateAction.Continue;
        }

        public static bool ShouldResolveMissingNames(
            int chatCount,
            bool resolveMissingAllowed,
            bool deferHeavyLocalWork) =>
            chatCount > 0 &&
            resolveMissingAllowed &&
            !deferHeavyLocalWork;
    }
}
