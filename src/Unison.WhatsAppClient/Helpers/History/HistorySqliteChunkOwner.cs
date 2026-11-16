// =============================================================================
// HistorySqliteChunkOwner
//
// Owns IHistorySqliteChunkStartedHost + IHistorySqliteChunkAppliedHost and the
// running count of conversations SQLite history applied this sync. First-sync
// flags, progress publish and the last-sync stamps stay as ports.
// =============================================================================
using System;
using System.Threading;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.History;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistorySqliteChunkOwner :
        IHistorySqliteChunkStartedHost,
        IHistorySqliteChunkAppliedHost
    {
        public delegate void PublishProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage);

        private readonly SqliteHistoryQuietFinalize _quietFinalize;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<int> _finalizeQuietMs;
        private readonly Func<int> _fullFinalizeDelayMs;
        private readonly Func<bool> _firstSyncListAlreadyReleased;
        private readonly Action<string> _noteCatchUpProgress;
        private readonly PublishProgress _publishProgress;
        private readonly Action _markHistorySyncReceivedNow;
        private readonly Action<string> _rememberLastSyncType;
        private readonly Action<string> _completeUserResyncWait;
        private readonly Action<string> _log;
        private int _conversationsAccumulated;

        public HistorySqliteChunkOwner(
            SqliteHistoryQuietFinalize quietFinalize,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<int> finalizeQuietMs,
            Func<int> fullFinalizeDelayMs,
            Func<bool> firstSyncListAlreadyReleased,
            Action<string> noteCatchUpProgress,
            PublishProgress publishProgress,
            Action markHistorySyncReceivedNow,
            Action<string> rememberLastSyncType,
            Action<string> completeUserResyncWait,
            Action<string> log)
        {
            _quietFinalize = quietFinalize ?? throw new ArgumentNullException(nameof(quietFinalize));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _finalizeQuietMs = finalizeQuietMs ?? throw new ArgumentNullException(nameof(finalizeQuietMs));
            _fullFinalizeDelayMs = fullFinalizeDelayMs
                ?? throw new ArgumentNullException(nameof(fullFinalizeDelayMs));
            _firstSyncListAlreadyReleased = firstSyncListAlreadyReleased
                ?? throw new ArgumentNullException(nameof(firstSyncListAlreadyReleased));
            _noteCatchUpProgress = noteCatchUpProgress
                ?? throw new ArgumentNullException(nameof(noteCatchUpProgress));
            _publishProgress = publishProgress ?? throw new ArgumentNullException(nameof(publishProgress));
            _markHistorySyncReceivedNow = markHistorySyncReceivedNow
                ?? throw new ArgumentNullException(nameof(markHistorySyncReceivedNow));
            _rememberLastSyncType = rememberLastSyncType
                ?? throw new ArgumentNullException(nameof(rememberLastSyncType));
            _completeUserResyncWait = completeUserResyncWait
                ?? throw new ArgumentNullException(nameof(completeUserResyncWait));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        /// <summary>Conversations SQLite history applied since the last reset.</summary>
        public int ConversationsAccumulated
        {
            get => Volatile.Read(ref _conversationsAccumulated);
            set => Volatile.Write(ref _conversationsAccumulated, value);
        }

        public void OnChunkStarted(string syncType, int conversationCount) =>
            HistorySqliteChunkStartedDispatch.Run(this, syncType, conversationCount);

        public void OnChunkApplied(string syncType, int conversationCount) =>
            HistorySqliteChunkAppliedDispatch.Run(this, syncType, conversationCount);

        int IHistorySqliteChunkStartedHost.SqliteHistoryConversationsAccumulated => ConversationsAccumulated;

        bool IHistorySqliteChunkStartedHost.FirstSyncListAlreadyReleased => _firstSyncListAlreadyReleased();

        void IHistorySqliteChunkStartedHost.NoteFullHistoryCatchUpProgress(string reason) =>
            _noteCatchUpProgress(reason);

        void IHistorySqliteChunkStartedHost.BumpSqliteHistoryFinalizeGeneration() =>
            _quietFinalize.BumpGeneration();

        void IHistorySqliteChunkStartedHost.PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage) =>
            _publishProgress(isActive, isCompleted, processed, total, stage);

        int IHistorySqliteChunkAppliedHost.SqliteHistoryFullFinalizeDelayMs => _fullFinalizeDelayMs();

        int IHistorySqliteChunkAppliedHost.SqliteHistoryFinalizeQuietMs => _finalizeQuietMs();

        bool IHistorySqliteChunkAppliedHost.IsEnrichmentAwaitingHeavyHistory =>
            _heavyHistoryEnrichment.IsAwaitingHeavyHistory;

        int IHistorySqliteChunkAppliedHost.SqliteHistoryConversationsAccumulated => ConversationsAccumulated;

        bool IHistorySqliteChunkAppliedHost.FirstSyncListAlreadyReleased => _firstSyncListAlreadyReleased();

        void IHistorySqliteChunkAppliedHost.MarkHistorySyncReceivedNow() => _markHistorySyncReceivedNow();

        void IHistorySqliteChunkAppliedHost.NoteFullHistoryCatchUpProgress(string reason) =>
            _noteCatchUpProgress(reason);

        int IHistorySqliteChunkAppliedHost.AddSqliteHistoryConversations(int count)
        {
            int next = Math.Max(0, ConversationsAccumulated) + count;
            ConversationsAccumulated = next;
            return next;
        }

        void IHistorySqliteChunkAppliedHost.PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage) =>
            _publishProgress(isActive, isCompleted, processed, total, stage);

        int IHistorySqliteChunkAppliedHost.NextSqliteHistoryFinalizeGeneration() =>
            _quietFinalize.NextGeneration();

        void IHistorySqliteChunkAppliedHost.FinalizeSqliteHistoryAfterQuiet(
            int generation,
            int delayMs,
            int processed) =>
            _quietFinalize.ScheduleAfterQuiet(generation, delayMs, processed);

        void IHistorySqliteChunkAppliedHost.CompleteUserResyncHistoryWait(string reason) =>
            _completeUserResyncWait(reason);

        void IHistorySqliteChunkAppliedHost.RememberLastHistorySyncType(string type) =>
            _rememberLastSyncType(type);

        void IHistorySqliteChunkAppliedHost.ReleaseEnrichmentAfterHeavyHistory(string type) =>
            _heavyHistoryEnrichment.Run(type);

        void IHistorySqliteChunkAppliedHost.LogHistorySqliteChunkApplied(
            string type,
            int count,
            int accumulated) =>
            _log(
                "History SQLite path applied (type=" + type +
                ", conversations=" + count +
                ", accumulated=" + accumulated + ").");
    }
}
