// =============================================================================
// OfflineReplayFlush
//
// Owns the offline-replay pending drain: flush lock, idle timer, claim/drain on
// PendingMessageQueue, and ApplyFlushAction / FlushAsync entry points.
// WhatsAppService only supplies SQLite / catalogue I/O via IOfflineReplayFlushIoHost.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Messages
{
    public sealed class OfflineReplayFlush :
        IOfflineReplayFlushHost,
        IScheduleOfflineReplayFlushTimerHost,
        IPendingMessageFlushHost
    {
        private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromMilliseconds(750);

        private readonly PendingMessageQueue _pending;
        private readonly IOfflineReplayFlushIoHost _io;
        private readonly SemaphoreSlim _flushLock = new SemaphoreSlim(1, 1);
        private readonly object _timerLock = new object();
        private readonly int _dueMs;
        private Timer _timer;

        public OfflineReplayFlush(
            PendingMessageQueue pending,
            IOfflineReplayFlushIoHost io,
            TimeSpan? flushInterval = null)
        {
            _pending = pending ?? throw new ArgumentNullException(nameof(pending));
            _io = io ?? throw new ArgumentNullException(nameof(io));
            TimeSpan interval = flushInterval ?? DefaultFlushInterval;
            _dueMs = (int)interval.TotalMilliseconds;
        }

        public int PendingCount => _pending.PendingCount;

        public bool IsFlushClaimed => _pending.IsFlushClaimed;

        public List<ChatMessage> SnapshotFor(string chatJid, Func<string, string> canonicalize) =>
            _pending.SnapshotFor(chatJid, canonicalize);

        public PendingFlushAction Enqueue(
            string jid,
            IEnumerable<ChatMessage> messages,
            DateTime utcNow,
            bool scheduleFlush = true)
        {
            PendingFlushAction action = _pending.Add(jid, messages, utcNow, scheduleFlush);
            ApplyFlushAction(action);
            return action;
        }

        public void ApplyFlushAction(PendingFlushAction action) =>
            OfflineReplayFlushDispatch.ApplyAction(this, action);

        public Task FlushOfflineReplayAsync(string reason) =>
            OfflineReplayFlushDispatch.FlushAsync(this, reason);

        /// <summary>Stops the idle timer (shutdown / suspend hygiene).</summary>
        public void ClearTimer() => ClearFlushTimer();

        // --- IPendingMessageFlushHost ----------------------------------------

        void IPendingMessageFlushHost.ApplyFlushAction(PendingFlushAction action) =>
            ApplyFlushAction(action);

        Task IPendingMessageFlushHost.FlushOfflineReplayAsync(string reason) =>
            FlushOfflineReplayAsync(reason);

        // --- IScheduleOfflineReplayFlushTimerHost ----------------------------

        int IScheduleOfflineReplayFlushTimerHost.OfflineReplayFlushDueMs => _dueMs;

        void IScheduleOfflineReplayFlushTimerHost.ReplaceOfflineReplayFlushTimer(
            Action callback,
            int dueMs)
        {
            lock (_timerLock)
            {
                _timer?.Dispose();
                _timer = new Timer(
                    _ => callback(),
                    null,
                    dueMs,
                    Timeout.Infinite);
            }
        }

        // --- IOfflineReplayFlushHost -----------------------------------------

        Task IOfflineReplayFlushHost.EnterFlushLockAsync() => _flushLock.WaitAsync();

        void IOfflineReplayFlushHost.ReleaseFlushLock() => _flushLock.Release();

        PendingMessageDrain IOfflineReplayFlushHost.DrainPending(DateTime utcNow) =>
            _pending.Drain(utcNow);

        void IOfflineReplayFlushHost.ClearFlushTimer() => ClearFlushTimer();

        List<ChatMessage> IOfflineReplayFlushHost.SelectBatch(IEnumerable<ChatMessage> queued) =>
            _pending.SelectBatch(queued);

        Task IOfflineReplayFlushHost.PersistLiveMessagesAsync(
            string chatJid,
            IList<ChatMessage> messages) =>
            _io.PersistLiveMessagesAsync(chatJid, messages);

        Task IOfflineReplayFlushHost.RemovePendingOutgoingAsync(IEnumerable<string> ids) =>
            _io.RemovePendingOutgoingAsync(ids);

        Task IOfflineReplayFlushHost.RemovePendingIncomingAsync(IEnumerable<string> ids) =>
            _io.RemovePendingIncomingAsync(ids);

        void IOfflineReplayFlushHost.RestoreDrain(PendingMessageDrain drain) =>
            _pending.Restore(drain);

        void IOfflineReplayFlushHost.LogFlushed(
            int saved,
            int chatCount,
            int dirtyChatCount,
            string reason) =>
            _io.LogFlushed(saved, chatCount, dirtyChatCount, reason);

        void IOfflineReplayFlushHost.ScheduleCatalogPersist() =>
            _io.ScheduleCatalogPersist();

        void IOfflineReplayFlushHost.RecordFlushDeferred(
            Exception ex,
            string reason,
            int chatCount) =>
            _io.RecordFlushDeferred(ex, reason, chatCount);

        bool IOfflineReplayFlushHost.CompleteFlush() =>
            _pending.CompleteFlush();

        void IOfflineReplayFlushHost.ScheduleFlushTimer() =>
            ScheduleOfflineReplayFlushTimerDispatch.Run(
                this,
                () => _ = OfflineReplayFlushDispatch.OnIdleTimerAsync(this));

        bool IOfflineReplayFlushHost.TryClaimFlush() =>
            _pending.TryClaimFlush();

        void IOfflineReplayFlushHost.LogNonFatalIdleFlushFailure(Exception ex) =>
            _io.LogNonFatalIdleFlushFailure(ex);

        void IOfflineReplayFlushHost.StartFlush(string reason) =>
            _ = FlushOfflineReplayAsync(reason);

        private void ClearFlushTimer()
        {
            lock (_timerLock)
            {
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
