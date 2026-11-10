using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Uwp.Services;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IChatCatalogPersistence
    {
        private void LoadHistoryFreshnessRepairState() =>
            _freshnessRepair.Load();

        private void PersistFullHistoryRepairCompletedUtc(DateTime timestampUtc) =>
            _freshnessRepair.PersistFullHistoryRepairCompletedUtc(timestampUtc);

        private void PersistFreshnessReconnectFallbackUtc(DateTime timestampUtc) =>
            _freshnessRepair.PersistFreshnessReconnectFallbackUtc(timestampUtc);

        internal void EnableScheduledPersist(string reason) =>
            ChatCatalogPersistDispatch.EnableAfterStartup(this, reason);

        /// <summary>
        /// Loads compact chat metadata for the UI. Safe to run after the connection has
        /// already started; only one caller performs the disk read.
        /// </summary>
        public Task LoadPersistedUiStateAsync() =>
            _persistedUiState.RunAsync();

        private Task PersistBackgroundDisplayNamesAsync() =>
            _backgroundDisplayNames.PersistAsync();

        private void QueueOfflineReplayMessageForPersist(string jid, ChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            QueueMessagesForPersist(jid, new[] { message });
        }

        private List<ChatMessage> GetPendingPersistMessagesSnapshot(string chatJid)
        {
            return _pendingMessages.SnapshotFor(chatJid, jid => GetCanonicalJid(NormalizeJid(jid)));
        }

        private Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages) =>
            _liveMessages.PersistAsync(chatJid, messages);

        private void QueueMessagesForPersist(string jid, IEnumerable<ChatMessage> messages, bool queueIncomingJournal = true, bool scheduleFlush = true)
        {
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return;
            }

            _offlineReplayFlush.Enqueue(jid, messages, DateTime.UtcNow, scheduleFlush);
        }

        /// <summary>
        /// Drains the pending queue to SQLite. Suspension does not come through here:
        /// it flushes the append-only journal and skips the per-chat rewrite that would
        /// blow the suspend deadline.
        /// </summary>
        private Task FlushOfflineReplayMessagesAsync(string reason) =>
            _offlineReplayFlush.FlushOfflineReplayAsync(reason);

        /// <summary>
        /// Persists current chats and contact metadata to disk.
        /// </summary>
        public Task<bool> PersistDataAsync() =>
            _catalogDisk.PersistFullAsync();

        /// <summary>
        /// Writes only the given chat-list rows to <c>history_chat_preview</c>. Used after
        /// mark-read so clearing a badge does not rewrite the whole catalogue and contact maps.
        /// </summary>
        private Task PersistChatCatalogSliceAsync(IList<ChatItem> chats) =>
            _catalogDisk.PersistSliceAsync(chats);

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        internal void SchedulePersist() =>
            ChatCatalogPersistDispatch.Schedule(this);

        PersistScheduleAction IChatCatalogPersistHost.RequestPersist() =>
            _persistScheduler.Request();

        void IChatCatalogPersistHost.LogDeferredDuringWarmup() =>
            Debug.WriteLine("[WhatsAppService] SchedulePersist skipped during startup warm-up");

        void IChatCatalogPersistHost.ArmDebounceTimer() =>
            _catalogDebounce.Arm(
                ChatCatalogPersistDispatch.DebounceMilliseconds,
                () => _ = ChatCatalogPersistDispatch.OnDebounceElapsedAsync(this));

        bool IChatCatalogPersistHost.TryBeginPersist() =>
            _persistScheduler.TryBeginPersist();

        Task<bool> IChatCatalogPersistHost.RunFullPersistAsync() =>
            PersistDataAsync();

        void IChatCatalogPersistHost.RestorePersistDebt() =>
            _persistScheduler.Restore();

        PersistEnableResult IChatCatalogPersistHost.EnableAfterStartup() =>
            _persistScheduler.EnableAfterStartup();

        void IChatCatalogPersistHost.LogSuppressionLifted(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] Startup persist suppression lifted: " + reason);

        void IChatCatalogPersistHost.LogFlushingDeferred(string reason) =>
            Debug.WriteLine(
                "[WhatsAppService] Flushing deferred persist after startup warm-up: " +
                reason);

        void IChatCatalogPersistence.Schedule() => SchedulePersist();

        void IChatCatalogPersistence.PersistRows(IList<ChatItem> chats)
        {
            if (chats == null || chats.Count == 0)
            {
                return;
            }

            // Author-strip + tip + OnDemand LID sweeps used to fire PersistRows(1) dozens
            // of times under chat-open; each took the catalog write lock beside history apply.
            if (_rowPersistBuffer.Enqueue(chats) <= 0)
            {
                return;
            }

            _rowPersistDebounce.Arm(
                ChatCatalogRowPersistBuffer.DebounceMilliseconds,
                FlushPendingCatalogRows);
        }

        private void FlushPendingCatalogRows()
        {
            IList<ChatItem> slice = _rowPersistBuffer.TakeAll();
            if (slice == null || slice.Count == 0)
            {
                return;
            }

            _ = PersistChatCatalogSliceAsync(slice);
        }

        private Task PersistChatCatalogAsync(IList<ChatItem> chats) =>
            PersistChatCatalogAsync(ChatCatalogSnapshot.Take(chats));

        private Task PersistChatCatalogAsync(ChatCatalogSnapshot snapshot) =>
            ChatCatalogPreviewPersist.UpsertAsync(_chatPreviews, snapshot);

        private Task LoadPersistedChatsAsync() =>
            _catalogDisk.LoadPersistedChatsAsync();

        private Task PersistSuspendTailAsync() =>
            _catalogDisk.PersistSuspendTailAsync();
    }
}
