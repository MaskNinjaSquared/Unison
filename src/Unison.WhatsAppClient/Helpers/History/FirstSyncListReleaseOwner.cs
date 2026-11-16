// =============================================================================
// FirstSyncListReleaseOwner
//
// Owns IFirstSyncListReleaseHost and the "first-sync unlock in flight" latch.
// Hold flags, progress publish and deferred avatars stay ports.
// =============================================================================
using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class FirstSyncListReleaseOwner : IFirstSyncListReleaseHost
    {
        /// <summary>Quiet gap after first-sync list unlock before deferred avatar wake.</summary>
        public static readonly TimeSpan DeferredAvatarDelayAfterUnlock = TimeSpan.FromSeconds(2);

        private readonly HistorySqliteChunkOwner _historyChunks;
        private readonly ReleaseEnrichmentAfterHeavyHistoryCoordinator _heavyHistoryEnrichment;
        private readonly Func<IContactService> _contacts;
        private readonly Func<bool> _isFirstSyncListHoldInProgress;
        private readonly Action _markReleaseDeferred;
        private readonly HistorySqliteChunkOwner.PublishProgress _publishProgress;
        private readonly Action<string, TimeSpan> _scheduleDeferredAvatars;
        private readonly Action<string> _trySchedulePostMessageEnrichment;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly Action<string> _log;
        private int _unlockInFlight;

        public FirstSyncListReleaseOwner(
            HistorySqliteChunkOwner historyChunks,
            ReleaseEnrichmentAfterHeavyHistoryCoordinator heavyHistoryEnrichment,
            Func<IContactService> contacts,
            Func<bool> isFirstSyncListHoldInProgress,
            Action markReleaseDeferred,
            HistorySqliteChunkOwner.PublishProgress publishProgress,
            Action<string, TimeSpan> scheduleDeferredAvatars,
            Action<string> trySchedulePostMessageEnrichment,
            IRuntimeDiagnostics diagnostics,
            Action<string> log)
        {
            _historyChunks = historyChunks ?? throw new ArgumentNullException(nameof(historyChunks));
            _heavyHistoryEnrichment = heavyHistoryEnrichment
                ?? throw new ArgumentNullException(nameof(heavyHistoryEnrichment));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
            _isFirstSyncListHoldInProgress = isFirstSyncListHoldInProgress
                ?? throw new ArgumentNullException(nameof(isFirstSyncListHoldInProgress));
            _markReleaseDeferred = markReleaseDeferred
                ?? throw new ArgumentNullException(nameof(markReleaseDeferred));
            _publishProgress = publishProgress ?? throw new ArgumentNullException(nameof(publishProgress));
            _scheduleDeferredAvatars = scheduleDeferredAvatars
                ?? throw new ArgumentNullException(nameof(scheduleDeferredAvatars));
            _trySchedulePostMessageEnrichment = trySchedulePostMessageEnrichment
                ?? throw new ArgumentNullException(nameof(trySchedulePostMessageEnrichment));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _log = log ?? throw new ArgumentNullException(nameof(log));
        }

        public void Schedule(string stage, int processed, int total) =>
            FirstSyncListReleaseDispatch.Schedule(this, stage, processed, total);

        /// <summary>Drops the in-flight latch (session reset).</summary>
        public void ResetUnlockInFlight() => Interlocked.Exchange(ref _unlockInFlight, 0);

        bool IFirstSyncListReleaseHost.IsFirstSyncListHoldInProgress => _isFirstSyncListHoldInProgress();

        bool IFirstSyncListReleaseHost.IsFirstSyncUnlockInFlight =>
            Interlocked.CompareExchange(ref _unlockInFlight, 0, 0) == 1;

        bool IFirstSyncListReleaseHost.TryClaimFirstSyncUnlockInFlight() =>
            Interlocked.Exchange(ref _unlockInFlight, 1) == 0;

        void IFirstSyncListReleaseHost.MarkFirstSyncListReleaseDeferred() => _markReleaseDeferred();

        void IFirstSyncListReleaseHost.PublishInitialSyncProgress(
            bool isActive,
            bool isCompleted,
            int processed,
            int total,
            string stage) =>
            _publishProgress(isActive, isCompleted, processed, total, stage);

        void IFirstSyncListReleaseHost.WriteFirstSyncNamesJournal(string stage, int processed) =>
            _diagnostics.Write(
                "history",
                "first-sync-names-before-list",
                "stage=" + (stage ?? string.Empty) + "; processed=" + processed);

        void IFirstSyncListReleaseHost.LogFirstSyncListRelease(string message) => _log(message);

        async Task IFirstSyncListReleaseHost.ResolveNamesBeforeListReleaseAsync()
        {
            // Runs after list unlock. force: gate would otherwise skip during replay.
            // includeGroupListing: false — QueryAllGroups belongs to the groups phase
            // (embedding it here starved Mobile ~200s). Placeholders resolve in usync lots;
            // group subjects follow the dedicated listing pass.
            IContactService contacts = _contacts();
            if (contacts == null)
            {
                return;
            }

            await contacts.ResolveMissingNamesAsync(force: true, includeGroupListing: false)
                .ConfigureAwait(false);
            await contacts.RefreshContactNamesAsync(includeGroups: false, force: true)
                .ConfigureAwait(false);
        }

        void IFirstSyncListReleaseHost.RecordFirstSyncNamesException(Exception error, string stage) =>
            _diagnostics.RecordException(
                "history",
                "first-sync-names-before-list-failed",
                error,
                "stage=" + (stage ?? string.Empty));

        void IFirstSyncListReleaseHost.ClearSqliteHistoryConversationsAccumulated() =>
            _historyChunks.ConversationsAccumulated = 0;

        void IFirstSyncListReleaseHost.ScheduleDeferredAvatarsAfterUnlock(string stage) =>
            _scheduleDeferredAvatars(
                "after-first-sync-unlock:" + (stage ?? string.Empty),
                DeferredAvatarDelayAfterUnlock);

        void IFirstSyncListReleaseHost.ClearEnrichmentAwaitingHeavyHistory() =>
            _heavyHistoryEnrichment.SetAwaitingHeavyHistory(false);

        void IFirstSyncListReleaseHost.TrySchedulePostMessageEnrichment(string reason) =>
            _trySchedulePostMessageEnrichment(reason);

        void IFirstSyncListReleaseHost.ClearFirstSyncUnlockInFlight() => ResetUnlockInFlight();

        void IFirstSyncListReleaseHost.FireAndForgetFirstSyncUnlock(Func<Task> work) =>
            _ = Task.Run(work);
    }
}
