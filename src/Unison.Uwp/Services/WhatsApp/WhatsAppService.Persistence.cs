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
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IChatCatalogPersistence
    {
        internal void EnableScheduledPersist(string reason) =>
            ChatCatalogPersistDispatch.EnableAfterStartup(this, reason);

        /// <summary>
        /// Loads compact chat metadata for the UI. Safe to run after the connection has
        /// already started; only one caller performs the disk read.
        /// </summary>
        public Task LoadPersistedUiStateAsync() =>
            _persistedUiState.RunAsync();

        private void QueueOfflineReplayMessageForPersist(string jid, ChatMessage message)
        {
            if (message == null)
            {
                return;
            }

            QueueMessagesForPersist(jid, new[] { message });
        }

        private void QueueMessagesForPersist(string jid, IEnumerable<ChatMessage> messages, bool queueIncomingJournal = true, bool scheduleFlush = true)
        {
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return;
            }

            _offlineReplayFlush.Enqueue(jid, messages, DateTime.UtcNow, scheduleFlush);
        }

        /// <summary>
        /// Persists current chats and contact metadata to disk.
        /// </summary>
        public Task<bool> PersistDataAsync() =>
            _catalogDisk.PersistFullAsync();

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

            _ = _catalogDisk.PersistSliceAsync(slice);
        }

        private Task PersistChatCatalogAsync(IList<ChatItem> chats) =>
            PersistChatCatalogAsync(ChatCatalogSnapshot.Take(chats));

        private Task PersistChatCatalogAsync(ChatCatalogSnapshot snapshot) =>
            ChatCatalogPreviewPersist.UpsertAsync(_chatPreviews, snapshot);

        async Task IConversationResyncHost.ResyncChatListAppStateFlagsAsync()
        {
            try
            {
                var bridge = _socketSlot.Current as SocketBridge;
                if (bridge == null)
                {
                    return;
                }

                await bridge.ResyncAppStateReportingAllAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] ResyncChatListAppStateFlags failed: " + ex.Message);
            }
        }

        // --- IOfflineReplayFlushIoHost ---------------------------------------

        Task IOfflineReplayFlushIoHost.PersistLiveMessagesAsync(
            string chatJid,
            IList<ChatMessage> messages) =>
            _liveMessages.PersistAsync(chatJid, messages);

        Task IOfflineReplayFlushIoHost.RemovePendingOutgoingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingOutgoingAsync(ids);

        Task IOfflineReplayFlushIoHost.RemovePendingIncomingAsync(IEnumerable<string> ids) =>
            _messageStore.RemovePendingIncomingAsync(ids);

        void IOfflineReplayFlushIoHost.ScheduleCatalogPersist() => SchedulePersist();

        void IOfflineReplayFlushIoHost.LogFlushed(
            int saved,
            int chatCount,
            int dirtyChatCount,
            string reason) =>
                    Debug.WriteLine(
                "[WhatsAppService] Flushed " + saved + " queued message(s) across " +
                chatCount + " chat(s), dirtyChats=" + dirtyChatCount + ", reason=" + reason);

        void IOfflineReplayFlushIoHost.RecordFlushDeferred(
            Exception ex,
            string reason,
            int chatCount) =>
            _diagnostics.RecordException(
                "messages",
                "message-batch-flush-deferred",
                ex,
                "reason=" + reason + "; chats=" + chatCount);

        void IOfflineReplayFlushIoHost.LogNonFatalIdleFlushFailure(Exception ex) =>
                        Debug.WriteLine(
                "[WhatsAppService] Non-fatal message batch flush failure: " + ex.Message);
    }
}