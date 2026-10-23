using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Helpers;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    /// <summary>
    /// The quiet period between a finished replay and the enrichment work that follows it, plus
    /// the diagnostics that make that period explainable.
    /// </summary>
    /// <remarks>
    /// Startup enrichment used to sleep a flat 25 seconds on Windows Mobile and then give up
    /// silently if the device was not on its lowest memory level. From the outside that read as a
    /// minute of nothing followed by names appearing for no reason - so the wait now ends as soon
    /// as the sync actually settles, the pressure check retries instead of abandoning the pass, and
    /// both report what they are doing. Quiet floors and enrichment budgets follow
    /// <c>SyncBudget</c> (memory + hot sync), not a Mobile/desktop flag.
    /// </remarks>
    public partial class WhatsAppService
    {
        /// <summary>Breathing room the UI gets before enrichment starts, even on a fast device.</summary>
        private TimeSpan StartupQuietFloor =>
            TimeSpan.FromMilliseconds(CurrentSyncPacing.StartupQuietFloorMs);

        /// <summary>
        /// Holds off enrichment until the list stops moving, up to <paramref name="budget"/>.
        /// Polls rather than sleeping the whole budget: on a device that settles in two seconds
        /// there is nothing left to protect, and the old flat sleep was pure dead time.
        /// </summary>
        /// <returns>False when cancelled.</returns>
        private Task<bool> WaitForStartupQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token)
        {
            // Do not paint "Finishing startup…" over an open conversation.
            Action begin = string.IsNullOrWhiteSpace(_activeChat.Jid)
                ? (Action)(() => RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.Settling)))
                : null;

            return StartupQuietGate.WaitUntilQuietAsync(
                budget,
                StartupQuietFloor,
                token,
                () => _initialSync.IsActive || IsReplayDrainActive,
                begin,
                () => RaiseSyncStatus(null),
                (waitedMs, budgetMs) => RuntimeDiagnosticsService.Instance.Write(
                    "startup-phase",
                    "quiet-wait",
                    "reason=" + (reason ?? string.Empty) +
                    "; waitedMs=" + waitedMs +
                    "; budgetMs=" + budgetMs +
                    "; frugal=" + UseFrugalSyncBudget +
                    "; memory=" + _appMemory.UsageLevel));
        }

        /// <summary>
        /// Waits for the device to fall back to low memory usage before an enrichment pass.
        /// </summary>
        /// <remarks>
        /// This used to be a bare early return, which meant a device that happened to be above the
        /// low watermark at that instant never resolved its names at all - the chats simply stayed
        /// on their phone numbers until some other path stumbled over them.
        /// </remarks>
        /// <returns>True when there is headroom; false when every retry was spent or cancelled.</returns>
        private Task<bool> WaitForMemoryHeadroomAsync(string reason, CancellationToken token)
        {
            return StartupQuietGate.WaitForMemoryHeadroomAsync(
                token,
                () => _appMemory.IsLow,
                attempt =>
                {
                    RaiseSyncStatus(SyncPhaseStatus.Format(SyncPhaseStatus.LowMemory));
                    RuntimeDiagnosticsService.Instance.Write(
                        "startup-phase",
                        "memory-retry",
                        "reason=" + (reason ?? string.Empty) +
                        "; attempt=" + attempt +
                        "; level=" + _appMemory.UsageLevel);
                },
                () => RuntimeDiagnosticsService.Instance.Write(
                    "startup-phase",
                    "memory-abandoned",
                    "reason=" + (reason ?? string.Empty) +
                    "; level=" + _appMemory.UsageLevel));
        }

        /// <summary>
        /// Brackets a startup phase in the runtime journal with its duration and the memory level
        /// at both ends, so an exported report shows where the seconds went.
        /// </summary>
        private IDisposable TraceStartupPhase(string phase)
        {
            return new StartupPhaseTrace(phase, _appMemory);
        }
    }
}
