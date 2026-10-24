using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IChatCatalogPersistence, IChatIdentityPersistence, IChatCatalogFullPersistHost, ILoadPersistedChatCatalogHost
    {

        private void LoadHistoryFreshnessRepairState()
        {
            try
            {
                var settings = LocalSettingsAccess.Current;
                string rawText = settings.Get<string>(LocalSettingsConstants.LastFullHistoryRepairCompletedUtc);
                if (!string.IsNullOrEmpty(rawText) &&
                    DateTime.TryParse(rawText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
                {
                    _lastFullHistoryRepairCompletedUtc = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
                    Debug.WriteLine($"[WhatsAppService] Loaded full-history repair completed timestamp: {_lastFullHistoryRepairCompletedUtc:O}");
                }

                string reconnectRawText = settings.Get<string>(LocalSettingsConstants.LastFreshnessReconnectFallbackUtc);
                if (!string.IsNullOrEmpty(reconnectRawText) &&
                    DateTime.TryParse(reconnectRawText, null, System.Globalization.DateTimeStyles.RoundtripKind, out var reconnectParsed))
                {
                    _lastFreshnessReconnectFallbackUtc = reconnectParsed.Kind == DateTimeKind.Utc ? reconnectParsed : reconnectParsed.ToUniversalTime();
                    Debug.WriteLine($"[WhatsAppService] Loaded freshness reconnect fallback timestamp: {_lastFreshnessReconnectFallbackUtc:O}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to load history freshness repair state: {ex.Message}");
            }
        }

        private void PersistFullHistoryRepairCompletedUtc(DateTime timestampUtc)
        {
            _lastFullHistoryRepairCompletedUtc = timestampUtc;
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.LastFullHistoryRepairCompletedUtc,
                    timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist full-history repair completed timestamp: {ex.Message}");
            }
        }

        private void PersistFreshnessReconnectFallbackUtc(DateTime timestampUtc)
        {
            _lastFreshnessReconnectFallbackUtc = timestampUtc;
            try
            {
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.LastFreshnessReconnectFallbackUtc,
                    timestampUtc.ToString("O"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist freshness reconnect fallback timestamp: {ex.Message}");
            }
        }

        internal void EnableScheduledPersist(string reason) =>
            ChatCatalogPersistDispatch.EnableAfterStartup(this, reason);

        /// <summary>
        /// Loads compact chat metadata for the UI. Safe to run after the connection has
        /// already started; only one caller performs the disk read.
        /// </summary>
        public async Task LoadPersistedUiStateAsync()
        {
            await InitializeConnectionStateAsync();
            if (_persistedUiStateLoaded || _authState?.Registered != true)
            {
                return;
            }

            await _persistedUiLoadLock.WaitAsync();
            try
            {
                if (_persistedUiStateLoaded) return;
                await LoadPersistedChatsAsync();
                _persistedUiStateLoaded = true;
                RuntimeDiagnosticsService.Instance.Write(
                    "startup",
                    "persisted-ui-loaded",
                    "chatRows=" + Chats.Count);

                // Local avatar files are free for the socket; bind them before history catch-up
                // or enrichment so the list shows faces while tips/names settle.
                try
                {
                    if (Contacts == null)
                    {
                        Debug.WriteLine(
                            "[WhatsAppService] Avatar hydrate skipped (persisted-ui-loaded): contact facade missing");
                    }
                    else
                    {
                        await Contacts.HydrateCachedAvatarUrisAsync("persisted-ui-loaded")
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exHydrate)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Startup avatar hydrate failed: " + exHydrate.Message);
                }

                // Catalog came from history_chat_preview; fix Last Message from history_message
                // when the preview row is stale (deferred maintenance may have run with empty Chats).
                try
                {
                    if (ChatFacade != null)
                    {
                        await ChatFacade.ReconcileChatPreviewsFromSqliteAsync(null, "persisted-ui-loaded")
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception exReconcile)
                {
                    Debug.WriteLine(
                        "[WhatsAppService] Startup preview reconcile failed: " + exReconcile.Message);
                }
            }
            finally
            {
                _persistedUiLoadLock.Release();
            }
        }

        private async Task PersistBackgroundDisplayNamesAsync()
        {
            try
            {
                // Copies first: these are live UI-thread collections.
                var displayNames = BackgroundDisplayNameTable.Build(
                    Chats.ToList(),
                    ContactNames.ToList(),
                    PhoneContactNamesByJid.ToList(),
                    JidAlias.ToList());

                await BackgroundDisplayNameStore.SaveAsync(
                    displayNames,
                    _authState?.Me?.Id,
                    _authState?.Me?.Lid);
                RuntimeDiagnosticsService.Instance.Write(
                    "socket-broker",
                    "display-name-snapshot-persisted",
                    "count=" + displayNames.Count);
            }
            catch (Exception nameSnapshotError)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "socket-broker",
                    "display-name-snapshot-failed",
                    nameSnapshotError);
            }
        }

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

        private async Task PersistLiveMessagesAsync(string chatJid, IList<ChatMessage> messages)
        {
            if (_historyMessages == null || string.IsNullOrWhiteSpace(chatJid) || messages == null)
            {
                return;
            }

            var list = new List<ChatMessage>();
            for (int i = 0; i < messages.Count; i++)
            {
                if (messages[i] != null)
                {
                    list.Add(messages[i]);
                }
            }

            if (list.Count == 0)
            {
                return;
            }

            try
            {
                await _historyMessages.UpsertLiveMessagesAsync(NormalizeJid(chatJid), list).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] SQLite live persist failed: " + ex.Message);
                throw;
            }
        }

        private void QueueMessagesForPersist(string jid, IEnumerable<ChatMessage> messages, bool queueIncomingJournal = true, bool scheduleFlush = true)
        {
            if (string.IsNullOrWhiteSpace(jid) || messages == null)
            {
                return;
            }

            var action = _pendingMessages.Add(jid, messages, DateTime.UtcNow, scheduleFlush);
            OfflineReplayFlushDispatch.ApplyAction(this, action);
        }

        /// <summary>
        /// Drains the pending queue to SQLite. Suspension does not come through here:
        /// it flushes the append-only journal and skips the per-chat rewrite that would
        /// blow the suspend deadline.
        /// </summary>
        private Task FlushOfflineReplayMessagesAsync(string reason) =>
            OfflineReplayFlushDispatch.FlushAsync(this, reason);

        /// <summary>
        /// Persists current chats and contact metadata to disk.
        /// </summary>
        public Task<bool> PersistDataAsync() =>
            ChatCatalogFullPersistDispatch.RunFullAsync(this);

        Task IChatCatalogFullPersistHost.EnterPersistLockAsync() =>
            _persistRunLock.WaitAsync();

        void IChatCatalogFullPersistHost.ExitPersistLock() =>
            _persistRunLock.Release();

        async Task IChatCatalogFullPersistHost.PersistFullCatalogAndNameMapsAsync()
        {
            // No sync line here: mark-read and avatar/name enrichment used to surface
            // "Saving chats..." on the Mobile status bar for the whole catalog rewrite, which
            // looked like opening a chat was blocked on disk. Sync stages already report
            // through SyncPhaseStatus; this path is housekeeping.

            // Messages are persisted by the batched message queue. Rewriting every
            // loaded chat file here caused long UI stalls and large allocation spikes.
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> contactSnapshot = null;
            Dictionary<string, string> phoneContactSnapshot = null;
            Dictionary<string, string> aliasSnapshot = null;
            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(Chats);
                contactSnapshot = ContactNames.Snapshot();
                phoneContactSnapshot = PhoneContactNamesByJid.Snapshot();
                aliasSnapshot = JidAlias.Snapshot();
            });

            IEnumerable<string> chatJids = catalog?.ChatJids ?? (IEnumerable<string>)new string[0];
            await PersistChatCatalogAsync(catalog).ConfigureAwait(false);
            await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), chatJids);
            await _messageStore.SavePhoneContactNamesAsync(phoneContactSnapshot ?? new Dictionary<string, string>(), chatJids);
            await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), chatJids);

            Debug.WriteLine(
                "[WhatsAppService] Persisted " + (catalog?.Rows.Count ?? 0) +
                " chat rows and contact metadata");
        }

        void IChatCatalogFullPersistHost.LogFullPersistFailed(Exception error) =>
            Debug.WriteLine(
                "[WhatsAppService] Failed to persist data: " +
                (error != null ? error.Message : string.Empty));

        async Task IChatCatalogFullPersistHost.PersistCriticalSuspendStateAsync()
        {
            ChatCatalogSnapshot catalog = null;
            Dictionary<string, string> aliasSnapshot = null;
            await _ui.RunAsync(() =>
            {
                catalog = ChatCatalogSnapshot.Take(Chats);
                aliasSnapshot = JidAlias.Snapshot();
            });

            await PersistChatCatalogAsync(catalog).ConfigureAwait(false);
            await _messageStore.SaveJidAliasesAsync(
                aliasSnapshot ?? new Dictionary<string, string>(),
                catalog?.ChatJids ?? (IEnumerable<string>)new string[0]);

            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "fast-suspend-persisted",
                "chatRows=" + (catalog?.Rows.Count ?? 0));
        }

        void IChatCatalogFullPersistHost.LogSuspendPersistFailed(Exception error) =>
            RuntimeDiagnosticsService.Instance.RecordException(
                "lifecycle",
                "suspend-persist-tail-failed",
                error);

        /// <summary>
        /// Writes only the given chat-list rows to <c>history_chat_preview</c>. Used after
        /// mark-read so clearing a badge does not rewrite the whole catalogue and contact maps.
        /// </summary>
        private Task PersistChatCatalogSliceAsync(IList<ChatItem> chats) =>
            ChatCatalogFullPersistDispatch.RunSliceAsync(this, chats);

        Task IChatCatalogFullPersistHost.PersistCatalogSliceAsync(IList<ChatItem> chats) =>
            PersistChatCatalogAsync(chats);

        void IChatCatalogFullPersistHost.LogCatalogSliceSucceeded(int rowCount) =>
            Debug.WriteLine("[WhatsAppService] Persisted chat-preview slice rows=" + rowCount);

        void IChatCatalogFullPersistHost.LogCatalogSliceFailed(Exception error) =>
            Debug.WriteLine(
                "[WhatsAppService] Chat-preview slice persist failed: " +
                (error != null ? error.Message : string.Empty));

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        internal void SchedulePersist() =>
            ChatCatalogPersistDispatch.Schedule(this);

        PersistScheduleAction IChatCatalogPersistHost.RequestPersist() =>
            _persistScheduler.Request();

        void IChatCatalogPersistHost.LogDeferredDuringWarmup() =>
            Debug.WriteLine("[WhatsAppService] SchedulePersist skipped during startup warm-up");

        void IChatCatalogPersistHost.ArmDebounceTimer()
        {
            lock (_persistLock)
            {
                _persistTimer?.Dispose();
                _persistTimer = new System.Threading.Timer(
                    _ => _ = ChatCatalogPersistDispatch.OnDebounceElapsedAsync(this),
                    null,
                    ChatCatalogPersistDispatch.DebounceMilliseconds,
                    Timeout.Infinite);
            }
        }

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
        void IChatIdentityPersistence.PersistNow(string reason) =>
            _ = PersistChatIdentityStateAsync(reason ?? "facade");


        void IChatCatalogPersistence.PersistRows(IList<ChatItem> chats)
        {
            if (chats == null || chats.Count == 0)
            {
                return;
            }

            // Copy: callers often pass live UI rows that may mutate before the write runs.
            var slice = new List<ChatItem>(chats.Count);
            for (int i = 0; i < chats.Count; i++)
            {
                if (chats[i] != null)
                {
                    slice.Add(chats[i]);
                }
            }

            if (slice.Count == 0)
            {
                return;
            }

            _ = PersistChatCatalogSliceAsync(slice);
        }

        private Task PersistChatCatalogAsync(IList<ChatItem> chats)
        {
            return PersistChatCatalogAsync(ChatCatalogSnapshot.Take(chats));
        }

        private async Task PersistChatCatalogAsync(ChatCatalogSnapshot snapshot)
        {
            if (_chatPreviews == null || snapshot == null || snapshot.Rows.Count == 0)
            {
                return;
            }

            await _chatPreviews.UpsertManyAsync(snapshot.Rows, notifyChunk: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads persisted chats from disk on startup.
        /// </summary>
        private Task LoadPersistedChatsAsync() =>
            LoadPersistedChatCatalogDispatch.RunAsync(this);

        IDisposable ILoadPersistedChatCatalogHost.BeginPersistedChatsLoad() =>
            _chatState.BeginPersistedChatsLoad();

        Task<IReadOnlyList<HistoryChatPreview>> ILoadPersistedChatCatalogHost.ReadAllPreviewsAsync() =>
            _chatPreviews.GetAllAsync();

        Task ILoadPersistedChatCatalogHost.ApplyPreviewsToChatListAsync(
            IReadOnlyList<HistoryChatPreview> stored)
        {
            return _ui.RunAsync(() =>
            {
                var existing = new HashSet<string>(
                    Chats.Where(c => c != null && !string.IsNullOrWhiteSpace(c.JID))
                         .Select(c => NormalizeJid(c.JID)),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var preview in stored)
                {
                    ChatItem chat = HistoryChatPreviewApplier.ToChatItemForCatalog(preview);
                    if (chat == null || string.IsNullOrWhiteSpace(chat.JID))
                    {
                        continue;
                    }

                    string normJid = NormalizeJid(chat.JID);
                    chat.JID = normJid;
                    ChatPreviewNormalizer.ApplyToChatItem(chat);
                    ApplyChatKind(chat);
                    if (existing.Add(normJid))
                    {
                        Chats.Add(chat);
                    }
                }

                ApplyChatKindsToAll();
                ChatDisplayOrder.SortInPlace(Chats);
            });
        }

        void ILoadPersistedChatCatalogHost.LogLoaded(int previewCount) =>
            Debug.WriteLine(
                "[WhatsAppService] Fast startup loaded " + previewCount + " chat preview rows");

        void ILoadPersistedChatCatalogHost.NotifyHistorySyncAfterLoad() =>
            History?.NotifyHistorySyncReceived(null);

        void ILoadPersistedChatCatalogHost.LogLoadFailed(Exception error) =>
            Debug.WriteLine(
                "[WhatsAppService] Failed to load persisted chat metadata: " +
                (error != null ? error.Message : string.Empty));

        private Task PersistSuspendTailAsync() =>
            ChatCatalogFullPersistDispatch.RunSuspendTailAsync(this);
    }
}
