// =============================================================================
// StartupMaintenanceCoordinator
//
// Unison product decisions for deferred startup maintenance: visibility /
// memory gate and whether the contact pass may resolve missing names. The
// WhatsApp client executes; this type decides from IStartupMaintenanceTuning
// plus live facts (window, chat count, catch-up).
// =============================================================================
using System;
using Unison.Core.Contracts.Tuning;
using Unison.Core.Helpers;

namespace Unison.Core.Product
{
    public enum DeferredStartupGateAction
    {
        Continue,
        SkipVisibility,
        CheckMemory
    }

    public sealed class StartupMaintenanceCoordinator
    {
        private readonly IStartupMaintenanceTuning _tuning;

        public StartupMaintenanceCoordinator(IStartupMaintenanceTuning tuning)
        {
            _tuning = tuning ?? throw new ArgumentNullException(nameof(tuning));
        }

        public DeferredStartupGateAction DecideGate(bool windowVisible)
        {
            if (_tuning.DeferredStartupRequiresVisibleWindow && !windowVisible)
            {
                return DeferredStartupGateAction.SkipVisibility;
            }

            return _tuning.DeferredStartupRequiresMemoryHeadroom
                ? DeferredStartupGateAction.CheckMemory
                : DeferredStartupGateAction.Continue;
        }

        public bool ShouldResolveMissingNames(int chatCount, bool historyCatchUpActive) =>
            chatCount > 0 &&
            _tuning.ResolveMissingNamesAtStartup &&
            !HistoryCatchUpLocalWorkPolicy.ShouldDeferHeavyLocalWork(historyCatchUpActive);
    }
}
