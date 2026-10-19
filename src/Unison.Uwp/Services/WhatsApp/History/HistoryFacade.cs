// =============================================================================
// HistoryFacade
//
// Everything the app asks the phone for about the past. Today that is the full
// resync - "forget everything and download it again" - and the on-demand fetch
// of older messages belongs here as it moves off WhatsAppService.
//
// The wipe half is local and the refill half is not: WhatsApp never re-runs the
// initial bootstrap for a companion that is already linked, so the only way back
// to a full history is to ask the phone for one and wait for it to answer.
//
// What the legacy path could not do was tell whether it had been answered. It
// sent the request, waited on a timer, and forced a reconnect when the timer ran
// out - guessing, because nothing tied the chunks that arrived to the request
// that asked for them. The socket rewrite carries that tie, so this waits on the
// answer itself and reports failure when there is none.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Proto;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.Socket.Events;
using Unison.Socket.Session;
using Unison.Socket.Sync;
using Unison.Uwp.Client;
using Unison.Uwp.Services.Socket;

namespace Unison.Uwp.Services.WhatsApp.History
{
    public sealed class HistoryFacade : IHistoryService
    {
        /// <summary>How long the phone gets to send anything at all before we call it a failure.</summary>
        private static readonly TimeSpan FirstChunkTimeout = TimeSpan.FromSeconds(75);

        /// <summary>
        /// A gap this long after chunks were flowing means the phone is done. It has no way to
        /// say so: the last chunk of a sync looks exactly like the ones before it.
        /// </summary>
        private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(30);

        /// <summary>A ceiling, so a phone that dribbles chunks forever cannot pin the overlay open.</summary>
        private static readonly TimeSpan HardTimeout = TimeSpan.FromMinutes(6);

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IWhatsAppService _appState;
        private readonly IConversationResyncHost _resync;
        private readonly IHistorySyncHost _historySync;
        private readonly IMessageStore _store;
        private readonly ILocalSettings _settings;
        private readonly IHistoryChatPreviewStore _chatPreviewStore;
        private readonly IHistoryMigrationStore _migrationStore;
        private readonly IHistoryMessageStore _messageHistoryStore;
        private readonly IHistoryStatusStore _statusStore;
        private readonly INotificationService _notifications;
        private readonly IStringResources _strings;
        private readonly HistoryOnDemandRegistry _onDemandRequests;
        private readonly HistoryOnDemandDispatch _onDemand;
        private readonly IChatStateStore _chatState;
        private readonly SyncStatusChannel _syncStatus;
        private readonly InitialSyncProgressState _initialSync;
        private readonly SyncBudget _syncBudget;
        private readonly SyncWorkloadStrategy _workload;
        private readonly IDisplayKeepAwakeService _displayKeepAwake;
        private readonly ChatListInitialPaintGate _listPaintGate;

        /// <summary>
        /// Session-scoped: one toast after repeated background history lots. Not reset until
        /// process exit (fields start false / 0 on next launch).
        /// </summary>
        private bool _historySyncNotified;
        private int _historySyncBounceTimes;
        private readonly object _historySyncToastGate = new object();

        /// <summary>
        /// False until the SQLite warm says otherwise, so an unknown state never hides a list
        /// that is already there.
        /// </summary>
        private volatile bool _awaitingFirstHistorySync;
        private volatile bool _firstHistorySyncCompletedThisSession;

        internal HistoryFacade(
            IWhatsAppSessionProvider sessions,
            IWhatsAppService appState,
            IConversationResyncHost resync,
            IHistorySyncHost historySync,
            IMessageStore store,
            ILocalSettings settings,
            HistoryOnDemandRegistry onDemandRequests,
            IHistoryOnDemandSocket onDemandSocket,
            IChatStateStore chatState,
            SyncStatusChannel syncStatus,
            InitialSyncProgressState initialSync,
            SyncBudget syncBudget,
            SyncWorkloadStrategy workload,
            IHistoryChatPreviewStore chatPreviewStore = null,
            IHistoryMigrationStore migrationStore = null,
            IHistoryMessageStore messageHistoryStore = null,
            IHistoryStatusStore statusStore = null,
            INotificationService notifications = null,
            IStringResources strings = null,
            IDisplayKeepAwakeService displayKeepAwake = null,
            ChatListInitialPaintGate listPaintGate = null)
        {
            if (sessions == null)
            {
                throw new ArgumentNullException(nameof(sessions));
            }

            if (appState == null)
            {
                throw new ArgumentNullException(nameof(appState));
            }

            if (store == null)
            {
                throw new ArgumentNullException(nameof(store));
            }

            if (settings == null)
            {
                throw new ArgumentNullException(nameof(settings));
            }

            _sessions = sessions;
            _appState = appState;
            _resync = resync ?? throw new ArgumentNullException(nameof(resync));
            _historySync = historySync ?? throw new ArgumentNullException(nameof(historySync));
            _store = store;
            _settings = settings;
            _onDemandRequests = onDemandRequests ?? throw new ArgumentNullException(nameof(onDemandRequests));
            _onDemand = new HistoryOnDemandDispatch(
                onDemandRequests,
                onDemandSocket ?? throw new ArgumentNullException(nameof(onDemandSocket)),
                line => Debug.WriteLine("[HistoryFacade] " + line));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _syncStatus = syncStatus ?? throw new ArgumentNullException(nameof(syncStatus));
            _initialSync = initialSync ?? throw new ArgumentNullException(nameof(initialSync));
            _syncBudget = syncBudget ?? throw new ArgumentNullException(nameof(syncBudget));
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _chatPreviewStore = chatPreviewStore;
            _migrationStore = migrationStore;
            _messageHistoryStore = messageHistoryStore;
            _statusStore = statusStore;
            _notifications = notifications;
            _strings = strings;
            _displayKeepAwake = displayKeepAwake;
            _listPaintGate = listPaintGate;

            // Both live as long as the app does, so there is nothing to unhook from.
            _syncStatus.Changed += (s, status) =>
            {
                Debug.WriteLine("[HistoryFacade] SyncStatusChanged → " + (status ?? "<null>"));
                Relay(() => SyncStatusChanged?.Invoke(this, status), "SyncStatusChanged");
            };
            _appState.OnHistorySyncReceived += (s, sync) => Relay(() => HistorySyncReceived?.Invoke(this, sync), "HistorySyncReceived");
            _initialSync.Changed += (s, e) =>
            {
                // Before the relay: the list reads IsAwaitingFirstHistorySync inside its handler.
                // Release DisplayRequest immediately before clearing the hold so x:Load can run
                // without keeping the screen forced on through the paint storm.
                if (e != null && e.IsCompleted)
                {
                    _displayKeepAwake?.Release("before-list-xload");
                    _firstHistorySyncCompletedThisSession = true;
                    _awaitingFirstHistorySync = false;
                }

                Relay(() => InitialSyncProgress?.Invoke(this, e), "InitialSyncProgress");
            };
            _appState.OnSessionCleared += (s, e) => { _ = ResetHistorySqliteAsync("session-cleared"); };
            _appState.OnBackgroundHistorySyncBounced += (s, e) => NoteBackgroundHistorySyncBounce();
            if (_chatPreviewStore != null)
            {
                _chatPreviewStore.ChunkPersisted += (s, e) =>
                    Relay(() => ChatPreviewChunkPersisted?.Invoke(this, e), "ChatPreviewChunkPersisted");
            }

            if (_messageHistoryStore != null)
            {
                _messageHistoryStore.ChunkPersisted += (s, e) =>
                    Relay(() => HistoryMessageChunkPersisted?.Invoke(this, e), "HistoryMessageChunkPersisted");
            }

            _ = WarmSqliteStoresAsync();
        }

        public event EventHandler<string> SyncStatusChanged;

        // ---------------------------------------------------------------------
        // How the sync is going
        //
        // Read-only, and forwarded: the counters are produced where the chunks are applied. They
        // are on this facade because every one of them answers a question about the past arriving,
        // and the chat list should not have to name the client to ask it.
        // ---------------------------------------------------------------------

        public bool IsLoadingPersistedChats => _chatState.IsLoadingPersistedChats;

        public bool IsInitialSyncSafeMode => _initialSync.IsActive;

        public bool IsAwaitingFirstHistorySync => _awaitingFirstHistorySync;

        public bool PreferFrugalSyncBudget => _syncBudget.IsFrugal;

        public int InitialSyncProcessedConversations => _initialSync.ProcessedConversations;

        public int InitialSyncTotalConversations => _initialSync.TotalConversations;

        public event EventHandler<global::Proto.HistorySync> HistorySyncReceived;

        public event EventHandler<InitialSyncProgressEventArgs> InitialSyncProgress;

        public event EventHandler<HistoryChatPreviewChunkEventArgs> ChatPreviewChunkPersisted;

        public event EventHandler<HistoryMessageChunkEventArgs> HistoryMessageChunkPersisted;

        private static void Relay(Action raise, string name)
        {
            try
            {
                raise();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] " + name + " handler failed: " + ex.Message);
            }
        }

        public async Task TrackHistoryChunkStartedAsync(string syncType)
        {
            if (_migrationStore == null || IsOnDemandSyncType(syncType))
            {
                return;
            }

            try
            {
                await _migrationStore.MarkInProgressAsync(CurrentSyncId(), syncType ?? string.Empty, "history-sync")
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] TrackHistoryChunkStarted failed: " + ex.Message);
            }
        }

        public async Task TrackHistoryChunkCompletedAsync(string syncType, int conversationCount)
        {
            if (_migrationStore == null || IsOnDemandSyncType(syncType))
            {
                return;
            }

            if (conversationCount <= 0 &&
                (syncType == null ||
                 syncType.IndexOf("Full", StringComparison.OrdinalIgnoreCase) < 0))
            {
                return;
            }

            try
            {
                await _migrationStore.MarkSucceededAsync(
                        CurrentSyncId(),
                        syncType ?? string.Empty,
                        Math.Max(0, conversationCount))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] TrackHistoryChunkCompleted failed: " + ex.Message);
            }
        }

        public async Task ResetHistorySqliteAsync(string reason = null)
        {
            string resetReason = reason ?? string.Empty;
            _firstHistorySyncCompletedThisSession = false;
            _listPaintGate?.Reset("session-reset:" + resetReason);
            BeginFirstSyncListHold("session-reset:" + resetReason);
            if (_migrationStore != null)
            {
                try
                {
                    await _migrationStore.ResetAsync(resetReason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] Migration reset failed: " + ex.Message);
                }
            }

            if (_chatPreviewStore != null)
            {
                try
                {
                    await _chatPreviewStore.ClearAsync(resetReason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] Preview clear failed: " + ex.Message);
                }
            }

            if (_messageHistoryStore != null)
            {
                try
                {
                    await _messageHistoryStore.ClearAsync(resetReason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] History message clear failed: " + ex.Message);
                }
            }

            if (_statusStore != null)
            {
                try
                {
                    await _statusStore.ClearAsync(resetReason).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] History status clear failed: " + ex.Message);
                }
            }
        }

        /// <inheritdoc />
        public void NotifySqliteHistoryChunkApplied(string syncType, int conversationCount)
        {
            try
            {
                _historySync.NotifyHistorySqliteChunkApplied(syncType, conversationCount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] NotifySqliteHistoryChunkApplied failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Counts a background FULL_HISTORY solicitation. After more than two bounces in this
        /// process, shows one toast (no per-session reset — app restart clears it).
        /// </summary>
        private void NoteBackgroundHistorySyncBounce()
        {
            // Foreground already shows the catch-up banner; do not spend the one-toast budget.
            if (Unison.Uwp.App.IsWindowVisible)
            {
                return;
            }

            // First login / safe-mode catch-up: no toast popups while the list is still syncing.
            if (_initialSync != null && _initialSync.IsActive)
            {
                return;
            }

            bool shouldToast = false;
            int bounce;
            lock (_historySyncToastGate)
            {
                _historySyncBounceTimes++;
                bounce = _historySyncBounceTimes;

                // First two lots stay silent; from the third onward, one toast for the process.
                // No reset until the app process exits.
                if (!_historySyncNotified && bounce > 2)
                {
                    _historySyncNotified = true;
                    shouldToast = true;
                }
            }

            Debug.WriteLine(
                "[HistoryFacade] History sync bounce=" + bounce +
                ", notified=" + _historySyncNotified +
                ", toast=" + shouldToast);

            if (!shouldToast || _notifications == null)
            {
                return;
            }

            try
            {
                string title = _strings != null
                    ? _strings.Get("Toast_HistorySyncTitle", "Synchronizing history")
                    : "Synchronizing history";
                string body = _strings != null
                    ? _strings.Get(
                        "Toast_HistorySyncBody",
                        "Unison is catching up on conversations in the background.")
                    : "Unison is catching up on conversations in the background.";
                _notifications.ShowToast(title, body);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] History sync toast failed: " + ex.Message);
            }
        }

        /// <inheritdoc />
        public async Task<HistorySqliteChunkResult> PersistHistorySqliteChunkAsync(HistorySync sync)
        {
            var result = new HistorySqliteChunkResult
            {
                SyncType = sync?.SyncType.ToString() ?? string.Empty,
                ConversationCount = sync?.Conversations?.Count ?? 0
            };

            if (sync == null)
            {
                return result;
            }

            result.ConversationJids = CollectConversationJids(sync);

            try
            {
                ApplyLidMappingsFromHistory(sync);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] LID mappings from history failed: " + ex.Message);
            }

            try
            {
                // Banner before SQLite work: Mobile can spend seconds here with no UI otherwise.
                _historySync.NotifyHistorySqliteChunkStarted(result.SyncType, result.ConversationCount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] NotifyHistorySqliteChunkStarted failed: " + ex.Message);
            }

            try
            {
                await TrackHistoryChunkStartedAsync(result.SyncType).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Track started failed: " + ex.Message);
            }

            try
            {
                result.PreviewUpserted = await PersistChatPreviewsAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Preview persist failed: " + ex.Message);
            }

            try
            {
                PersistMessagesResult messages = await PersistMessagesAsync(sync).ConfigureAwait(false);
                result.MessageUpserted = messages.Upserted;
                result.MessageChatJids = messages.ChatJids ?? Array.Empty<string>();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Message persist failed: " + ex.Message);
                result.MessageChatJids = Array.Empty<string>();
            }

            try
            {
                await PersistStatusesAsync(sync).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Status persist failed: " + ex.Message);
            }

            if (result.IsOnDemand)
            {
                try
                {
                    // Prefer conversation ids (even when every message was filtered out).
                    IReadOnlyList<string> latchJids = result.ConversationJids.Count > 0
                        ? result.ConversationJids
                        : result.MessageChatJids;
                    _onDemandRequests.CompleteChats(latchJids.Select(JidHelper.Normalize).ToList());
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] CompleteHistoryOnDemand failed: " + ex.Message);
                }
            }

            bool isFullHistorySync = result.SyncType.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isFullHistorySync)
            {
                NoteBackgroundHistorySyncBounce();
            }

            NotifySqliteHistoryChunkApplied(result.SyncType, result.ConversationCount);

            try
            {
                if (result.ConversationCount > 0 || isFullHistorySync)
                {
                    await TrackHistoryChunkCompletedAsync(result.SyncType, result.ConversationCount)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Track completed failed: " + ex.Message);
            }

            return result;
        }

        private static IReadOnlyList<string> CollectConversationJids(HistorySync sync)
        {
            if (sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return Array.Empty<string>();
            }

            var jids = new List<string>(sync.Conversations.Count);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var conv in sync.Conversations)
            {
                if (conv == null || string.IsNullOrWhiteSpace(conv.Id))
                {
                    continue;
                }

                string jid = JidHelper.Normalize(conv.Id);
                if (string.IsNullOrWhiteSpace(jid) || !seen.Add(jid))
                {
                    continue;
                }

                jids.Add(jid);
            }

            return jids;
        }

        private void ApplyLidMappingsFromHistory(HistorySync sync)
        {
            if (sync == null)
            {
                return;
            }

            var pairs = new List<KeyValuePair<string, string>>();

            if (sync.PhoneNumberToLidMappings != null)
            {
                foreach (var mapping in sync.PhoneNumberToLidMappings)
                {
                    if (mapping == null ||
                        string.IsNullOrWhiteSpace(mapping.PnJid) ||
                        string.IsNullOrWhiteSpace(mapping.LidJid))
                    {
                        continue;
                    }

                    pairs.Add(new KeyValuePair<string, string>(
                        JidHelper.Normalize(mapping.LidJid),
                        JidHelper.Normalize(mapping.PnJid)));
                }
            }

            if (sync.Conversations != null)
            {
                foreach (var conv in sync.Conversations)
                {
                    if (conv == null ||
                        string.IsNullOrWhiteSpace(conv.LidJid) ||
                        string.IsNullOrWhiteSpace(conv.PnJid))
                    {
                        continue;
                    }

                    pairs.Add(new KeyValuePair<string, string>(
                        JidHelper.Normalize(conv.LidJid),
                        JidHelper.Normalize(conv.PnJid)));
                }
            }

            if (pairs.Count == 0)
            {
                return;
            }

            _historySync.ApplyHistoryLidMappings(pairs, "history-sqlite-chunk");
        }

        private async Task<int> PersistChatPreviewsAsync(HistorySync sync)
        {
            if (_chatPreviewStore == null || sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return 0;
            }

            string syncId = CurrentSyncId();
            IReadOnlyList<HistoryChatPreview> rows = await Task.Run(
                    () => HistoryChatPreviewBuilder.Build(sync, syncId ?? string.Empty))
                .ConfigureAwait(false);

            if (rows == null || rows.Count == 0)
            {
                return 0;
            }

            await _chatPreviewStore.UpsertManyAsync(rows).ConfigureAwait(false);
            return rows.Count;
        }

        private sealed class PersistMessagesResult
        {
            public int Upserted;
            public IReadOnlyList<string> ChatJids = Array.Empty<string>();
        }

        private async Task<PersistMessagesResult> PersistMessagesAsync(HistorySync sync)
        {
            var empty = new PersistMessagesResult();
            if (_messageHistoryStore == null || sync?.Conversations == null || sync.Conversations.Count == 0)
            {
                return empty;
            }

            string syncId = CurrentSyncId();
            int messageCap = _workload.MaxMessagesPerConversation;
            HistoryMessageWriteBatch batch = await Task.Run(
                    () => HistoryMessageBuilder.Build(sync, syncId ?? string.Empty, messageCap))
                .ConfigureAwait(false);

            if (batch == null || batch.IsEmpty)
            {
                return empty;
            }

            if (_workload.MaterializeHistoryThumbsDuringSync)
            {
                try
                {
                    await HistoryThumbnailMaterializer.MaterializeMessageThumbsAsync(batch.Messages)
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] Thumbnail materialize failed: " + ex.Message);
                }
            }
            else
            {
                DropInlineThumbnails(batch.Messages);
            }

            int upserted = 0;
            var chatJids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (HistoryMessageWriteBatch slice in HistoryPersistBatching.Split(
                batch,
                _workload.HistoryPersistSubBatchSize))
            {
                HistoryWriteBatchResult written = await _messageHistoryStore.PersistWriteBatchAsync(slice)
                    .ConfigureAwait(false);
                upserted += written?.UpsertedCount ?? 0;
                if (written?.ChatJids != null)
                {
                    for (int i = 0; i < written.ChatJids.Count; i++)
                    {
                        if (!string.IsNullOrWhiteSpace(written.ChatJids[i]))
                        {
                            chatJids.Add(written.ChatJids[i]);
                        }
                    }
                }
            }

            return new PersistMessagesResult
            {
                Upserted = upserted,
                ChatJids = chatJids.Count == 0 ? Array.Empty<string>() : chatJids.ToList()
            };
        }

        private static void DropInlineThumbnails(IList<HistoryMessage> messages)
        {
            if (messages == null)
            {
                return;
            }

            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null)
                {
                    messages[i].MediaThumbnailJpeg = null;
                }
            }
        }

        private async Task PersistStatusesAsync(HistorySync sync)
        {
            if (_statusStore == null || sync?.Conversations == null)
            {
                return;
            }

            string syncId = CurrentSyncId();
            IReadOnlyList<HistoryStatus> rows = await Task.Run(
                    () => HistoryStatusBuilder.Build(sync, syncId ?? string.Empty))
                .ConfigureAwait(false);

            if (rows == null || rows.Count == 0)
            {
                return;
            }

            Debug.WriteLine("[HistoryFacade] Status persist count=" + rows.Count + " sync=" + (syncId ?? ""));
            for (int i = 0; i < rows.Count && i < 12; i++)
            {
                Debug.WriteLine(
                    "[HistoryFacade] Status row " + StatusItemDiagnostics.DescribeItem(rows[i], i));
            }

            try
            {
                await HistoryThumbnailMaterializer.MaterializeStatusThumbsAsync(
                        rows as IList<HistoryStatus> ?? new List<HistoryStatus>(rows))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] Status thumbnail materialize failed: " + ex.Message);
            }

            await _statusStore.UpsertManyAsync(rows).ConfigureAwait(false);
        }

        private async Task WarmSqliteStoresAsync()
        {
            try
            {
                if (_migrationStore != null)
                {
                    await _migrationStore.InitializeAsync().ConfigureAwait(false);
                }

                if (_chatPreviewStore != null)
                {
                    await _chatPreviewStore.InitializeAsync().ConfigureAwait(false);
                }

                if (_messageHistoryStore != null)
                {
                    await _messageHistoryStore.InitializeAsync().ConfigureAwait(false);
                }

                if (_statusStore != null)
                {
                    await _statusStore.InitializeAsync().ConfigureAwait(false);
                }

                await ResolveAwaitingFirstHistorySyncAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] SQLite warm failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Awaiting only when no history batch ever succeeded and no list preview is stored:
        /// an older install with rows but a gate never marked must not get an empty list.
        /// </summary>
        private async Task ResolveAwaitingFirstHistorySyncAsync()
        {
            if (_migrationStore == null || _chatPreviewStore == null)
            {
                return;
            }

            HistoryMigrationState gate = await _migrationStore.GetAsync().ConfigureAwait(false);
            if (gate != null && gate.IsSucceeded)
            {
                return;
            }

            int storedPreviews = await _chatPreviewStore.CountAsync().ConfigureAwait(false);
            if (storedPreviews == 0 && !_firstHistorySyncCompletedThisSession)
            {
                BeginFirstSyncListHold("sqlite-warm-empty");
            }
        }

        /// <summary>
        /// List stays unmaterialized and the screen stays awake until quiet-finalize clears the hold.
        /// </summary>
        private void BeginFirstSyncListHold(string reason)
        {
            _awaitingFirstHistorySync = true;
            _listPaintGate?.Arm(reason);
            _displayKeepAwake?.Hold(reason);
        }

        private string CurrentSyncId()
        {
            try
            {
                return _settings.Get<string>(LocalSettingsConstants.MessageStoreSyncId) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsOnDemandSyncType(string syncType) =>
            HistoryOnDemandSyncType.Matches(syncType);

        /// <summary>
        /// Wipes the local conversations and asks the phone to send them again. The account stays
        /// linked; only what we hold locally is thrown away.
        /// </summary>
        public async Task ResyncConversationsAsync(IProgress<ConversationResyncPhase> progress = null)
        {
            var socket = _sessions.Socket;
            if (socket == null)
            {
                // No socket to correlate an answer through. The service's own resync knows how to
                // force a fresh transport first, which is the one thing worth trying from here.
                await ResetHistorySqliteAsync("resync-fallback").ConfigureAwait(false);
                await _resync.ResyncConversationsAsync(progress).ConfigureAwait(false);
                return;
            }

            progress?.Report(ConversationResyncPhase.CleaningHistory);
            await WipeLocalConversationsAsync().ConfigureAwait(false);

            progress?.Report(ConversationResyncPhase.PreparingConversations);
            _syncStatus.Raise("Re-syncing conversations...");

            try
            {
                await _appState.EnsureConnectedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not connect before requesting history: " + ex.Message);
                _syncStatus.Raise("Could not start history download. Try again.");
                return;
            }

            // Re-read both: connecting may well have replaced the session we checked above.
            socket = _sessions.Socket;
            var session = _sessions.Current;
            if (socket == null || session == null)
            {
                _syncStatus.Raise("Could not start history download. Try again.");
                return;
            }

            var answered = await RequestAndAwaitHistoryAsync(socket, session).ConfigureAwait(false);
            if (answered)
            {
                // The latch only exists to make a later connection retry a request that was never
                // answered. It was, so it would only cause a second full download.
                TrySetForceHistoryRepair(false);
                _syncStatus.Raise(null);
                return;
            }

            Debug.WriteLine("[ConversationResync] The phone did not answer with history.");
            _syncStatus.Raise("History did not arrive. It will be retried on the next connection.");
        }

        private async Task WipeLocalConversationsAsync()
        {
            await ResetHistorySqliteAsync("resync-wipe").ConfigureAwait(false);
            await _store.WipeChatsAndMessagesAsync().ConfigureAwait(false);
            await _resync.ClearConversationCachesAsync().ConfigureAwait(false);

            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not clear notifications: " + ex.Message);
            }

            // LT-hash still says app-state is current, so a normal sync would skip pin_v1.
            // Force a snapshot re-report so pins/mutes land again on the rebuilt list.
            try
            {
                await _resync.ResyncChatListAppStateFlagsAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] App-state flag resync failed: " + ex.Message);
            }

            // Set before the request rather than after it: if the app dies between the wipe and
            // the answer, this is what stops it from starting up permanently empty.
            TrySetForceHistoryRepair(true);
        }

        /// <inheritdoc />
        public Task<bool> RequestFullHistoryOnDemandAsync(string reason = null, bool force = false)
        {
            return _historySync.RequestFullHistoryOnDemandAsync(
                string.IsNullOrWhiteSpace(reason) ? "ui-full-history-on-demand" : reason,
                force);
        }

        /// <summary>
        /// Asks the phone for the page of <paramref name="jid"/> older than the oldest message
        /// in memory. True when a request went out.
        /// </summary>
        public Task<bool> EnsureChatPageAsync(string jid, int count)
        {
            string normJid = JidHelper.Normalize(jid);
            if (string.IsNullOrWhiteSpace(normJid))
            {
                return Task.FromResult(false);
            }

            return _onDemand.RequestChatPageAsync(normJid, _chatState.GetMessages(normJid), count, null, DateTime.UtcNow);
        }

        /// <summary>A page was asked for <paramref name="jid"/> and has not landed yet.</summary>
        public bool IsChatPagePending(string jid)
        {
            string normJid = JidHelper.Normalize(jid);
            return !string.IsNullOrWhiteSpace(normJid) && _onDemandRequests.IsChatPending(normJid);
        }

        /// <summary>
        /// Sends the request and waits for the history it produces, rather than for a fixed time.
        /// </summary>
        private async Task<bool> RequestAndAwaitHistoryAsync(IWhatsAppSocket socket, WhatsAppSession session)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            long lastChunkTicks = 0;

            // The event bus rather than the socket's legacy history event: only the chunk the bus
            // publishes carries the session id, and that id is the whole reason this can wait for
            // an answer instead of for a clock.
            using (session.Events.Process(batch =>
            {
                MessagingHistorySet chunk;
                if (batch.TryGet(WaEventKind.MessagingHistorySet, out chunk) &&
                    BelongsToRequest(chunk, requestId))
                {
                    Interlocked.Exchange(ref lastChunkTicks, DateTime.UtcNow.Ticks);

                    // The phone says how far along it is, so we do not have to infer the end from
                    // silence when it bothers to tell us.
                    if (chunk.Progress.HasValue && chunk.Progress.Value >= 100)
                    {
                        completed.TrySetResult(true);
                    }
                }

                return Task.CompletedTask;
            }))
            {
                try
                {
                    await socket.RequestFullHistorySyncOnDemandAsync(null, requestId).ConfigureAwait(false);
                    Debug.WriteLine("[ConversationResync] Requested a full history sync, requestId=" + requestId);

                    return await WaitForChunksAsync(completed.Task, () => Interlocked.Read(ref lastChunkTicks))
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[ConversationResync] The history request failed: " + ex.Message);
                    return false;
                }
            }
        }

        private static async Task<bool> WaitForChunksAsync(Task<bool> completed, Func<long> lastChunkTicks)
        {
            var startedUtc = DateTime.UtcNow;

            while (DateTime.UtcNow - startedUtc < HardTimeout)
            {
                var finished = await Task.WhenAny(completed, Task.Delay(PollInterval)).ConfigureAwait(false);
                if (ReferenceEquals(finished, completed))
                {
                    return true;
                }

                var ticks = lastChunkTicks();
                if (ticks == 0)
                {
                    if (DateTime.UtcNow - startedUtc > FirstChunkTimeout)
                    {
                        return false;
                    }

                    continue;
                }

                if (DateTime.UtcNow - new DateTime(ticks, DateTimeKind.Utc) > IdleTimeout)
                {
                    return true;
                }
            }

            // Out of time, but history did arrive and is already on screen.
            return lastChunkTicks() != 0;
        }

        /// <summary>
        /// Whether a chunk is the answer to our request. The session id settles it when the phone
        /// echoes one; when it does not, everything just got wiped, so any history carrying
        /// conversations can only be what we asked for.
        /// </summary>
        private static bool BelongsToRequest(MessagingHistorySet chunk, string requestId)
        {
            if (chunk == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(chunk.PeerDataRequestSessionId))
            {
                return string.Equals(chunk.PeerDataRequestSessionId, requestId, StringComparison.Ordinal);
            }

            return chunk.Chats != null && chunk.Chats.Count > 0;
        }

        private void TrySetForceHistoryRepair(bool pending)
        {
            try
            {
                _settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, pending);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not update the force-history flag: " + ex.Message);
            }
        }
    }
}
