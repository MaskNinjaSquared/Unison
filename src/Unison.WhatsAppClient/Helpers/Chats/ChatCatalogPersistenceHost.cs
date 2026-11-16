using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Messages;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>
    /// Chat catalogue persistence (debounced full save, buffered row saves, suspend tail) and
    /// the queued-message flush that feeds it. Owns the scheduler, timers and buffers they share.
    /// </summary>
    public sealed class ChatCatalogPersistenceHost :
        IChatCatalogPersistence,
        IChatCatalogPersistHost,
        IOfflineReplayFlushIoHost
    {
        private const string LogPrefix = "[ChatCatalogPersistenceHost] ";

        private readonly IMessageStore _messageStore;
        private readonly LiveMessagePersist _liveMessages;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly PersistScheduler _persistScheduler = new PersistScheduler();
        private readonly ChatCatalogDebounceTimer _catalogDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogDebounceTimer _rowPersistDebounce = new ChatCatalogDebounceTimer();
        private readonly ChatCatalogRowPersistBuffer _rowPersistBuffer = new ChatCatalogRowPersistBuffer();
        private readonly ChatCatalogDiskPersistence _catalogDisk;
        private readonly OfflineReplayFlush _offlineReplayFlush;

        public ChatCatalogPersistenceHost(
            ChatStateStore chatState,
            JidAliasTable jidAlias,
            IMessageStore messageStore,
            IHistoryChatPreviewStore chatPreviews,
            IDispatcher ui,
            Func<IHistoryService> history,
            PendingMessageQueue pendingMessages,
            LiveMessagePersist liveMessages,
            IAppMemoryInfo appMemory,
            IRuntimeDiagnostics diagnostics)
        {
            _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
            _liveMessages = liveMessages ?? throw new ArgumentNullException(nameof(liveMessages));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _offlineReplayFlush = new OfflineReplayFlush(
                pendingMessages ?? throw new ArgumentNullException(nameof(pendingMessages)),
                this);
            _catalogDisk = new ChatCatalogDiskPersistence(
                chatState,
                jidAlias,
                _messageStore,
                chatPreviews,
                ui,
                history,
                _diagnostics,
                appMemory);
        }

        public OfflineReplayFlush OfflineReplayFlush => _offlineReplayFlush;

        public bool IsPersistPending => _persistScheduler.IsPending;

        public void EnableScheduledPersist(string reason) =>
            ChatCatalogPersistDispatch.EnableAfterStartup(this, reason);

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        public void SchedulePersist() =>
            ChatCatalogPersistDispatch.Schedule(this);

        /// <summary>
        /// Persists current chats and contact metadata to disk.
        /// </summary>
        public Task<bool> PersistDataAsync() =>
            _catalogDisk.PersistFullAsync();

        public Task LoadPersistedChatsAsync() => _catalogDisk.LoadPersistedChatsAsync();

        public Task PersistSuspendTailAsync() => _catalogDisk.PersistSuspendTailAsync();

        /// <summary>Flushes buffered rows, then drops both timers and the persist debt.</summary>
        public void ResetPersistScheduler()
        {
            FlushPendingCatalogRows();
            _catalogDebounce.Dispose();
            _rowPersistDebounce.Dispose();
            _persistScheduler.Reset();
        }

        // --- IChatCatalogPersistence -----------------------------------------

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

        // --- IChatCatalogPersistHost -----------------------------------------

        PersistScheduleAction IChatCatalogPersistHost.RequestPersist() =>
            _persistScheduler.Request();

        void IChatCatalogPersistHost.LogDeferredDuringWarmup() =>
            Debug.WriteLine(LogPrefix + "SchedulePersist skipped during startup warm-up");

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
            Debug.WriteLine(LogPrefix + "Startup persist suppression lifted: " + reason);

        void IChatCatalogPersistHost.LogFlushingDeferred(string reason) =>
            Debug.WriteLine(
                LogPrefix + "Flushing deferred persist after startup warm-up: " + reason);

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
                LogPrefix + "Flushed " + saved + " queued message(s) across " +
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
            Debug.WriteLine(LogPrefix + "Non-fatal message batch flush failure: " + ex.Message);
    }
}
