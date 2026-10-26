using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Catch-up I/O and session flags the history façade still needs from the connection client.
    /// </summary>
    /// <remarks>
    /// Idle-lot decide and adjacent FULL_HISTORY orchestration live on
    /// <see cref="IHistoryService"/>; this port is tip freshness, finish side effects,
    /// live FULL_HISTORY send, and journal.
    /// </remarks>
    public interface IHistoryCatchUpHost
    {
        /// <summary>Newest tip across stored timelines / list strips.</summary>
        DateTime GetNewestStoredMessageUtc();

        /// <summary>True when history still looks behind the freshness watermark.</summary>
        bool TryGetHistoryFreshnessStaleReason(DateTime nowUtc, out string reason);

        /// <summary>True while the catch-up banner is painted.</summary>
        bool IsCatchUpBannerActive { get; }

        /// <summary>False when reconnect is suppressed or the account is not registered.</summary>
        bool CanSolicitAdjacent { get; }

        /// <summary>Latches enrichment waiting on heavy history (diagnostics).</summary>
        void MarkEnrichmentAwaitingHeavyHistory();

        /// <summary>Ends catch-up continue state, banner, and optionally releases enrichment.</summary>
        void FinishContinue(string reason, bool releaseEnrichment);

        /// <summary>Persists that the next session should force a history repair.</summary>
        void MarkForceHistoryRepairPending(string reason);

        /// <summary>Starts the hydration fallback pipeline (fire-and-forget).</summary>
        void StartHydrationFallback(string reason);

        /// <summary>
        /// Asks FULL_HISTORY on the live socket when handshake-ready.
        /// False when the transport is down or the request failed.
        /// </summary>
        Task<bool> TryRequestAdjacentLiveFullHistoryAsync(string reason);

        /// <summary>Runtime journal line under the history category.</summary>
        void WriteHistoryJournal(string eventName, string detail);

        /// <summary>Runtime exception journal under the history category.</summary>
        void RecordHistoryException(string eventName, Exception error, string detail);
    }
}
