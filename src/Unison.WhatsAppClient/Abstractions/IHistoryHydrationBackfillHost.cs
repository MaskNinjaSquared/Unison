// =============================================================================
// IHistoryHydrationBackfillHost
//
// Side effects for HistoryHydrationBackfillDispatch (per-JID ON_DEMAND hydration).
// Lock / CAS stay on the host around RunAsync.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IHistoryHydrationBackfillHost
    {
        bool IsConnected { get; }

        int HydrationBatchSize { get; }

        int HydrationPageSize { get; }

        int HydrationMaxPagesPerChat { get; }

        IReadOnlyList<HistoryBackfillCandidate> BuildHydrationCandidates(DateTime nowUtc);

        int GetHydrationMessageCount(string jid);

        string NormalizeHydrationJid(string jid);

        Task<bool> RequestHydrationOnDemandAsync(string normJid, int pageSize, string triggerReason);

        Task WaitForHydrationSettleAsync();

        void LogHydrationSkippedNotConnected(string reason);

        void LogHydrationNoCandidates(string reason);

        void WriteHydrationStart(string reason, int chatCount, int batchSize, int maxPages);

        void WriteHydrationBatch(
            int batchIndex,
            int sentCount,
            int ramGrew,
            int hasMoreCount,
            HistoryBackfillContinueAction action);

        void LogHydrationDone(
            int requested,
            int synced,
            int hasMoreLeft,
            int failed);
    }
}
