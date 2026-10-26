// =============================================================================
// IPostMessageEnrichmentHost
//
// Side effects for PostMessageEnrichmentDispatch (names / contacts / avatars /
// groups after message maintenance).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Core.Contracts.WhatsApp
{
    public interface IPostMessageEnrichmentHost
    {
        bool EnrichmentAwaitingHeavyHistory { get; }

        bool IsHistoryCatchUpActive { get; }

        bool IsFullHistoryInFlight { get; }

        bool IsListPaintPending { get; }

        bool IsConnected { get; }

        bool IsWindowVisible { get; }

        bool SerializePostSyncEnrichment { get; }

        TimeSpan EnrichmentQuietWait { get; }

        void LogDeferUntilHeavyFinishes(string reason);

        void MarkAwaitingHeavyHistory();

        void WriteCatchUpDeferred(string reason, string catchUpDeferReason);

        void LogArmAfterFullHistory(string reason);

        void MarkAwaitingListPaint();

        void WriteListPaintDeferred(string reason);

        /// <summary>Starts the one-shot enrichment schedule (CAS + CTS + background run).</summary>
        void BeginEnrichmentSchedule(string reason);

        Task<bool> WaitForStartupQuietAsync(
            TimeSpan timeout,
            string phase,
            CancellationToken token);

        void ClearSyncStatus();

        Task ResolveMissingNamesAsync(CancellationToken token);

        Task RefreshContactNamesAsync(CancellationToken token);

        void TriggerBackgroundResolution();

        /// <summary>True when avatar fetch should wait; <paramref name="reason"/> is set.</summary>
        bool TryDeferAvatarFetch(out string reason);

        void ScheduleDeferredAvatars(string reason);

        Task RetrieveContactPicturesAsync(CancellationToken token);

        void NotifyGroupListingDue();

        void WriteComplete(string reason, bool serialized);

        void RecordFailed(Exception ex, string reason);
    }
}
