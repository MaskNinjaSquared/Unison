// =============================================================================
// IStartupMaintenanceTuning
//
// Requirements and windows for deferred startup and post-replay maintenance.
// The product layer decides (frugal mode, tier); the client gates on the answers.
// =============================================================================
using System;

namespace Unison.Core.Contracts.Tuning
{
    public interface IStartupMaintenanceTuning
    {
        /// <summary>Deferred startup repair is skipped while the window is hidden.</summary>
        bool DeferredStartupRequiresVisibleWindow { get; }

        /// <summary>Deferred startup repair waits for memory headroom (skips when there is none).</summary>
        bool DeferredStartupRequiresMemoryHeadroom { get; }

        /// <summary>Whether the startup contact pass may resolve missing names.</summary>
        bool ResolveMissingNamesAtStartup { get; }

        /// <summary>Startup quiet window before post-replay maintenance runs.</summary>
        TimeSpan PostReplayQuietWait { get; }

        /// <summary>Whether a drain of this size gets the delayed bulk tip scan.</summary>
        bool RunDelayedBulkAfterReplay(int offlineCount);
    }
}
