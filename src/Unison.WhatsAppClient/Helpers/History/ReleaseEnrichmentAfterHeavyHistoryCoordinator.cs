// =============================================================================
// ReleaseEnrichmentAfterHeavyHistoryCoordinator
//
// Owns the awaiting-heavy-history flag + IReleaseEnrichmentAfterHeavyHistoryHost.
// Catch-up / schedule stay on the client via ctor delegates.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class ReleaseEnrichmentAfterHeavyHistoryCoordinator :
        IReleaseEnrichmentAfterHeavyHistoryHost
    {
        private readonly Func<bool> _isHistoryCatchUpActive;
        private readonly Action<string> _trySchedulePostMessageEnrichment;
        private readonly IRuntimeDiagnostics _diagnostics;
        private int _awaitingHeavyHistory;

        public ReleaseEnrichmentAfterHeavyHistoryCoordinator(
            Func<bool> isHistoryCatchUpActive,
            Action<string> trySchedulePostMessageEnrichment,
            IRuntimeDiagnostics diagnostics = null)
        {
            _isHistoryCatchUpActive = isHistoryCatchUpActive
                ?? throw new ArgumentNullException(nameof(isHistoryCatchUpActive));
            _trySchedulePostMessageEnrichment = trySchedulePostMessageEnrichment
                ?? throw new ArgumentNullException(nameof(trySchedulePostMessageEnrichment));
            _diagnostics = diagnostics;
        }

        public bool IsAwaitingHeavyHistory =>
            Interlocked.CompareExchange(ref _awaitingHeavyHistory, 0, 0) == 1;

        public void SetAwaitingHeavyHistory(bool awaiting) =>
            Interlocked.Exchange(ref _awaitingHeavyHistory, awaiting ? 1 : 0);

        public void Run(string reason) =>
            ReleaseEnrichmentAfterHeavyHistoryDispatch.Run(this, reason);

        bool IReleaseEnrichmentAfterHeavyHistoryHost.IsHistoryCatchUpActive =>
            _isHistoryCatchUpActive();

        void IReleaseEnrichmentAfterHeavyHistoryHost.MarkEnrichmentAwaitingHeavyHistory() =>
            SetAwaitingHeavyHistory(true);

        void IReleaseEnrichmentAfterHeavyHistoryHost.ClearEnrichmentAwaitingHeavyHistory() =>
            SetAwaitingHeavyHistory(false);

        void IReleaseEnrichmentAfterHeavyHistoryHost.TrySchedulePostMessageEnrichment(
            string reason) =>
            _trySchedulePostMessageEnrichment(reason);

        void IReleaseEnrichmentAfterHeavyHistoryHost.LogEnrichmentStillDeferred(string reason) =>
            Debug.WriteLine(
                "[ReleaseEnrichmentAfterHeavyHistory] still deferred (catch-up active, " +
                reason + ")");

        void IReleaseEnrichmentAfterHeavyHistoryHost.WriteEnrichmentDeferredCatchUp(string reason)
        {
            try
            {
                _diagnostics?.Write(
                    "startup",
                    "post-message-enrichment-deferred",
                    "reason=" + (reason ?? string.Empty) + "; catchUp=True");
            }
            catch
            {
            }
        }
    }
}
