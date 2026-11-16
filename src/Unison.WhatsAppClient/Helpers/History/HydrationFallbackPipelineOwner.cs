// =============================================================================
// HydrationFallbackPipelineOwner
//
// Owns IHydrationFallbackPipelineHost: after FULL_HISTORY quiet fails while tips
// stay stale, runs per-JID ON_DEMAND hydration, then natural FULL_HISTORY again,
// with day retries until tips look fresh.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HydrationFallbackPipelineOwner : IHydrationFallbackPipelineHost
    {
        private readonly Func<bool> _isHistoryHydrationActive;
        private readonly TryGetHistoryStaleReason _tryGetStaleReason;
        private readonly Action<DateTime> _noteHydrationDayAttempt;
        private readonly Func<string, Task<int>> _startBackgroundHistoryBackfillAsync;
        private readonly Func<string, Task> _requestReconnectHistoryCatchUpAsync;
        private readonly Action<string> _scheduleHydrationDayRetryIfNeeded;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;

        public HydrationFallbackPipelineOwner(
            Func<bool> isHistoryHydrationActive,
            TryGetHistoryStaleReason tryGetStaleReason,
            Action<DateTime> noteHydrationDayAttempt,
            Func<string, Task<int>> startBackgroundHistoryBackfillAsync,
            Func<string, Task> requestReconnectHistoryCatchUpAsync,
            Action<string> scheduleHydrationDayRetryIfNeeded,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _isHistoryHydrationActive = isHistoryHydrationActive
                ?? throw new ArgumentNullException(nameof(isHistoryHydrationActive));
            _tryGetStaleReason = tryGetStaleReason
                ?? throw new ArgumentNullException(nameof(tryGetStaleReason));
            _noteHydrationDayAttempt = noteHydrationDayAttempt
                ?? throw new ArgumentNullException(nameof(noteHydrationDayAttempt));
            _startBackgroundHistoryBackfillAsync = startBackgroundHistoryBackfillAsync
                ?? throw new ArgumentNullException(nameof(startBackgroundHistoryBackfillAsync));
            _requestReconnectHistoryCatchUpAsync = requestReconnectHistoryCatchUpAsync
                ?? throw new ArgumentNullException(nameof(requestReconnectHistoryCatchUpAsync));
            _scheduleHydrationDayRetryIfNeeded = scheduleHydrationDayRetryIfNeeded
                ?? throw new ArgumentNullException(nameof(scheduleHydrationDayRetryIfNeeded));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public Task RunAsync(string reason) => HydrationFallbackPipelineDispatch.RunAsync(this, reason);

        bool IHydrationFallbackPipelineHost.IsHistoryHydrationActive => _isHistoryHydrationActive();

        bool IHydrationFallbackPipelineHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            _tryGetStaleReason(nowUtc, out staleReason);

        void IHydrationFallbackPipelineHost.NoteHydrationDayAttempt(DateTime nowUtc) =>
            _noteHydrationDayAttempt(nowUtc);

        void IHydrationFallbackPipelineHost.WriteHydrationFallbackStart(
            string reason,
            string staleReason) =>
            _diagnostics.Write(
                "history",
                "hydration-fallback-start",
                "reason=" + (reason ?? string.Empty) +
                "; stale=" + (staleReason ?? string.Empty));

        Task<int> IHydrationFallbackPipelineHost.StartBackgroundHistoryBackfillAsync(string reason) =>
            _startBackgroundHistoryBackfillAsync(reason);

        void IHydrationFallbackPipelineHost.FireAndForgetReconnectHistoryCatchUp(string reason) =>
            _ = _requestReconnectHistoryCatchUpAsync(reason);

        void IHydrationFallbackPipelineHost.ScheduleHydrationDayRetryIfNeeded(string reason) =>
            _scheduleHydrationDayRetryIfNeeded(reason);

        void IHydrationFallbackPipelineHost.LogHydrationFallback(string message) => _log(message);

        void IHydrationFallbackPipelineHost.RecordHydrationFallbackException(
            Exception error,
            string reason) =>
            _diagnostics.RecordException(
                "history",
                "hydration-fallback-failed",
                error,
                reason);
    }
}
