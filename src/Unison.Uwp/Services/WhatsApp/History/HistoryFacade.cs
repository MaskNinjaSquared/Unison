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
        private readonly IMessageStore _store;
        private readonly ILocalSettings _settings;
        private readonly IHistoryChatPreviewStore _chatPreviewStore;
        private readonly IHistoryMigrationStore _migrationStore;
        private readonly IHistoryMessageStore _messageHistoryStore;
        private readonly IHistoryStatusStore _statusStore;

        internal HistoryFacade(
            IWhatsAppSessionProvider sessions,
            IWhatsAppService appState,
            IMessageStore store,
            ILocalSettings settings,
            IHistoryChatPreviewStore chatPreviewStore = null,
            IHistoryMigrationStore migrationStore = null,
            IHistoryMessageStore messageHistoryStore = null,
            IHistoryStatusStore statusStore = null)
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
            _store = store;
            _settings = settings;
            _chatPreviewStore = chatPreviewStore;
            _migrationStore = migrationStore;
            _messageHistoryStore = messageHistoryStore;
            _statusStore = statusStore;

            // Both live as long as the app does, so there is nothing to unhook from.
            _appState.OnSyncStatus += (s, status) =>
            {
                Debug.WriteLine("[HistoryFacade] SyncStatusChanged → " + (status ?? "<null>"));
                Relay(() => SyncStatusChanged?.Invoke(this, status), "SyncStatusChanged");
            };
            _appState.OnHistorySyncReceived += (s, sync) => Relay(() => HistorySyncReceived?.Invoke(this, sync), "HistorySyncReceived");
            _appState.OnInitialSyncProgress += (s, e) => Relay(() => InitialSyncProgress?.Invoke(this, e), "InitialSyncProgress");
            _appState.OnSessionCleared += (s, e) => { _ = ResetHistorySqliteAsync("session-cleared"); };
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
                _appState.NotifyHistorySqliteChunkApplied(syncType, conversationCount);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] NotifySqliteHistoryChunkApplied failed: " + ex.Message);
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
                _appState.NotifyHistorySqliteChunkStarted(result.SyncType, result.ConversationCount);
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
                    _appState.CompleteHistoryOnDemandForChats(latchJids);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[HistoryFacade] CompleteHistoryOnDemand failed: " + ex.Message);
                }
            }

            bool isFullHistorySync = result.SyncType.IndexOf("Full", StringComparison.OrdinalIgnoreCase) >= 0;
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

            _appState.ApplyHistoryLidMappings(pairs, "history-sqlite-chunk");
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
            HistoryMessageWriteBatch batch = await Task.Run(
                    () => HistoryMessageBuilder.Build(sync, syncId ?? string.Empty))
                .ConfigureAwait(false);

            if (batch == null || batch.IsEmpty)
            {
                return empty;
            }

            await _messageHistoryStore.PersistWriteBatchAsync(batch).ConfigureAwait(false);

            var jids = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddJid(string jid)
            {
                if (string.IsNullOrWhiteSpace(jid) || !seen.Add(jid))
                {
                    return;
                }

                jids.Add(jid);
            }

            for (int i = 0; i < batch.Messages.Count; i++)
            {
                AddJid(batch.Messages[i]?.ChatJid);
            }

            for (int i = 0; i < batch.Reactions.Count; i++)
            {
                AddJid(batch.Reactions[i]?.ChatJid);
            }

            for (int i = 0; i < batch.Pins.Count; i++)
            {
                AddJid(batch.Pins[i]?.ChatJid);
            }

            for (int i = 0; i < batch.Revokes.Count; i++)
            {
                AddJid(batch.Revokes[i]?.ChatJid);
            }

            return new PersistMessagesResult
            {
                Upserted = batch.Messages.Count,
                ChatJids = jids
            };
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
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HistoryFacade] SQLite warm failed: " + ex.Message);
            }
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

        private static bool IsOnDemandSyncType(string syncType)
        {
            return !string.IsNullOrEmpty(syncType) &&
                   syncType.IndexOf("OnDemand", StringComparison.OrdinalIgnoreCase) >= 0;
        }

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
                await _appState.ResyncConversationsAsync(progress).ConfigureAwait(false);
                return;
            }

            progress?.Report(ConversationResyncPhase.CleaningHistory);
            await WipeLocalConversationsAsync().ConfigureAwait(false);

            progress?.Report(ConversationResyncPhase.PreparingConversations);
            _appState.RaiseSyncStatus("Re-syncing conversations...");

            try
            {
                await _appState.EnsureConnectedAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not connect before requesting history: " + ex.Message);
                _appState.RaiseSyncStatus("Could not start history download. Try again.");
                return;
            }

            // Re-read both: connecting may well have replaced the session we checked above.
            socket = _sessions.Socket;
            var session = _sessions.Current;
            if (socket == null || session == null)
            {
                _appState.RaiseSyncStatus("Could not start history download. Try again.");
                return;
            }

            var answered = await RequestAndAwaitHistoryAsync(socket, session).ConfigureAwait(false);
            if (answered)
            {
                // The latch only exists to make a later connection retry a request that was never
                // answered. It was, so it would only cause a second full download.
                TrySetForceHistoryRepair(false);
                _appState.RaiseSyncStatus(null);
                return;
            }

            Debug.WriteLine("[ConversationResync] The phone did not answer with history.");
            _appState.RaiseSyncStatus("History did not arrive. It will be retried on the next connection.");
        }

        private async Task WipeLocalConversationsAsync()
        {
            await ResetHistorySqliteAsync("resync-wipe").ConfigureAwait(false);
            await _store.WipeChatsAndMessagesAsync().ConfigureAwait(false);
            await _appState.ClearConversationCachesAsync().ConfigureAwait(false);

            try
            {
                NotificationService.Instance.ClearAll();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ConversationResync] Could not clear notifications: " + ex.Message);
            }

            // Set before the request rather than after it: if the app dies between the wipe and
            // the answer, this is what stops it from starting up permanently empty.
            TrySetForceHistoryRepair(true);
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
