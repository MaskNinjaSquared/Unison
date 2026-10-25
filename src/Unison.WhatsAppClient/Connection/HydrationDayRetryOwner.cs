// =============================================================================
// HydrationDayRetryOwner
//
// Owns hydration day-retry CTS, day attempts and day-anchor. WhatsAppService
// only supplies freshness / pipeline / stamp via IHydrationDayRetryIoHost
// (same shape as PostSyncMaintenanceOwner / SoftReconnectCoordinator).
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Connection
{
    public sealed class HydrationDayRetryOwner :
        IHydrationDayRetryHost,
        INoteHydrationDayAttemptHost
    {
        private readonly IHydrationDayRetryIoHost _io;
        private CancellationTokenSource _cts;
        private DateTime _dayAnchorUtc = DateTime.MinValue;
        private int _dayAttempts;

        public HydrationDayRetryOwner(IHydrationDayRetryIoHost io)
        {
            _io = io ?? throw new ArgumentNullException(nameof(io));
        }

        public int DayAttempts => Volatile.Read(ref _dayAttempts);

        public DateTime DayAnchorUtc => _dayAnchorUtc;

        public bool IsArmed => Volatile.Read(ref _cts) != null;

        public void ScheduleIfNeeded(string reason) =>
            HydrationDayRetryDispatch.Schedule(this, reason);

        public void NoteAttempt(DateTime nowUtc) =>
            NoteHydrationDayAttemptDispatch.Run(this, nowUtc);

        public void Cancel()
        {
            CancellationTokenSource prior = Interlocked.Exchange(ref _cts, null);
            if (prior == null)
            {
                return;
            }

            try
            {
                prior.Cancel();
            }
            catch
            {
            }

            try
            {
                prior.Dispose();
            }
            catch
            {
            }
        }

        // --- IHydrationDayRetryHost ------------------------------------------

        int IHydrationDayRetryHost.HydrationDayAttempts => DayAttempts;

        int IHydrationDayRetryHost.MaxHydrationDayAttempts =>
            HistoryHydrationFallbackDecision.DefaultMaxDayAttempts;

        TimeSpan IHydrationDayRetryHost.HydrationDayRetryInterval =>
            HistoryHydrationFallbackDecision.DefaultDayRetryInterval;

        bool IHydrationDayRetryHost.TryGetFreshnessStaleReason(
            DateTime nowUtc,
            out string staleReason) =>
            _io.TryGetFreshnessStaleReason(nowUtc, out staleReason);

        CancellationTokenSource IHydrationDayRetryHost.ReplaceHydrationDayRetryCts()
        {
            CancellationTokenSource prior = Interlocked.Exchange(ref _cts, null);
            try
            {
                prior?.Cancel();
            }
            catch
            {
            }

            try
            {
                prior?.Dispose();
            }
            catch
            {
            }

            var cts = new CancellationTokenSource();
            Interlocked.Exchange(ref _cts, cts);
            return cts;
        }

        void IHydrationDayRetryHost.LogHydrationDayRetry(string message) =>
            _io.LogHydrationDayRetry(message);

        void IHydrationDayRetryHost.FireAndForgetHydrationDayRetry(Func<Task> work) =>
            _ = Task.Run(work);

        Task IHydrationDayRetryHost.DelayHydrationDayRetryAsync(
            TimeSpan delay,
            CancellationToken token) =>
            Task.Delay(delay, token);

        Task IHydrationDayRetryHost.RunHydrationFallbackPipelineAsync(string reason) =>
            _io.RunHydrationFallbackPipelineAsync(reason);

        // --- INoteHydrationDayAttemptHost ------------------------------------

        DateTime INoteHydrationDayAttemptHost.HydrationDayAnchorUtc => _dayAnchorUtc;

        void INoteHydrationDayAttemptHost.ResetHydrationDay(DateTime dayUtc)
        {
            _dayAnchorUtc = dayUtc;
            Interlocked.Exchange(ref _dayAttempts, 0);
        }

        void INoteHydrationDayAttemptHost.IncrementHydrationDayAttempts() =>
            Interlocked.Increment(ref _dayAttempts);

        void INoteHydrationDayAttemptHost.StampLastHydrationFallbackUtc(DateTime nowUtc) =>
            _io.StampLastHydrationFallbackUtc(nowUtc);
    }
}
