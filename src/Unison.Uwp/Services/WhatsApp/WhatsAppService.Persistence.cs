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
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
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

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
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

        private void EnableScheduledPersist(string reason)
        {
            bool shouldFlushPendingPersist = false;
            if (_suppressStartupScheduledPersist)
            {
                _suppressStartupScheduledPersist = false;
                lock (_persistLock)
                {
                    shouldFlushPendingPersist = _persistPending;
                }
                Debug.WriteLine($"[WhatsAppService] Startup persist suppression lifted: {reason}");
            }

            if (shouldFlushPendingPersist)
            {
                Debug.WriteLine($"[WhatsAppService] Flushing deferred persist after startup warm-up: {reason}");
                SchedulePersist();
            }
        }

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

                // Catalog came from history_chat_preview; fix Last Message from history_message
                // when the preview row is stale (deferred maintenance may have run with empty Chats).
                try
                {
                    await ReconcileChatPreviewsFromSqliteAsync(null, "persisted-ui-loaded")
                        .ConfigureAwait(false);
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
                var displayNames =
                    new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                foreach (ChatItem chat in Chats.ToList())
                {
                    if (chat == null ||
                        string.IsNullOrWhiteSpace(chat.JID) ||
                        string.IsNullOrWhiteSpace(chat.Name))
                    {
                        continue;
                    }
                    displayNames[chat.JID] = chat.Name;
                }

                // Group participants are not necessarily present as chat rows.
                // Include names learned from WhatsApp and prefer the user's local
                // address-book label when both are available.
                foreach (var pair in ContactNames.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value) &&
                        !displayNames.ContainsKey(pair.Key))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }
                foreach (var pair in PhoneContactNamesByJid.ToList())
                {
                    if (!string.IsNullOrWhiteSpace(pair.Key) &&
                        !string.IsNullOrWhiteSpace(pair.Value))
                    {
                        displayNames[pair.Key] = pair.Value;
                    }
                }

                // Mirror known PN/LID aliases so the external envelope can resolve
                // whichever identity form the server used for this message.
                foreach (var alias in JidAlias.ToList())
                {
                    if (string.IsNullOrWhiteSpace(alias.Key) ||
                        string.IsNullOrWhiteSpace(alias.Value))
                    {
                        continue;
                    }

                    string name;
                    if (displayNames.TryGetValue(alias.Key, out name) &&
                        !displayNames.ContainsKey(alias.Value))
                    {
                        displayNames[alias.Value] = name;
                    }
                    else if (displayNames.TryGetValue(alias.Value, out name) &&
                             !displayNames.ContainsKey(alias.Key))
                    {
                        displayNames[alias.Key] = name;
                    }
                }

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
            string canonical = GetCanonicalJid(NormalizeJid(chatJid));
            var result = new List<ChatMessage>();

            lock (_offlineReplayPersistLock)
            {
                foreach (var pair in _offlineReplayPendingMessagesByChat)
                {
                    if (!string.Equals(GetCanonicalJid(NormalizeJid(pair.Key)), canonical, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (pair.Value != null)
                    {
                        result.AddRange(pair.Value.Where(m => m != null));
                    }
                }
            }

            return result;
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

            var batch = messages.Where(m => m != null).ToList();
            if (batch.Count == 0)
            {
                return;
            }

            bool shouldFlush = false;
            lock (_offlineReplayPersistLock)
            {
                if (!_offlineReplayPendingMessagesByChat.TryGetValue(jid, out var pending))
                {
                    pending = new List<ChatMessage>();
                    _offlineReplayPendingMessagesByChat[jid] = pending;
                }

                int addedToPending = 0;
                foreach (var message in batch)
                {
                    if (message == null) continue;

                    int existingIndex = !string.IsNullOrWhiteSpace(message.Id)
                        ? pending.FindIndex(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                        : -1;
                    if (existingIndex >= 0)
                    {
                        pending[existingIndex] = message;
                    }
                    else
                    {
                        pending.Add(message);
                        addedToPending++;
                    }
                }

                _offlineReplayDirtyChats.Add(jid);
                _offlineReplayPendingMessageCount += addedToPending;

                var now = DateTime.UtcNow;
                bool thresholdReached = _offlineReplayPendingMessageCount >= OfflineReplayFlushMessageThreshold ||
                    (_lastOfflineReplayFlushUtc != DateTime.MinValue &&
                     now - _lastOfflineReplayFlushUtc >= OfflineReplayFlushInterval);

                if (scheduleFlush && thresholdReached && !_offlineReplayFlushRequested)
                {
                    _offlineReplayFlushRequested = true;
                    shouldFlush = true;
                }
                else if (scheduleFlush)
                {
                    ScheduleOfflineReplayFlushTimer_NoLock();
                }

                if (_lastOfflineReplayFlushUtc == DateTime.MinValue)
                {
                    _lastOfflineReplayFlushUtc = now;
                }
            }

            if (shouldFlush)
            {
                _ = FlushOfflineReplayMessagesAsync("message-batch-threshold");
            }
        }

        private async Task FlushOfflineReplayMessagesAsync(string reason)
        {
            await _offlineReplayFlushLock.WaitAsync();
            try
            {
                Dictionary<string, List<ChatMessage>> snapshot;
                HashSet<string> dirtyChats;
                lock (_offlineReplayPersistLock)
                {
                    if (_offlineReplayPendingMessageCount == 0)
                    {
                        return;
                    }

                    snapshot = _offlineReplayPendingMessagesByChat.ToDictionary(
                        kvp => kvp.Key,
                        kvp => kvp.Value.ToList(),
                        StringComparer.OrdinalIgnoreCase);
                    dirtyChats = new HashSet<string>(_offlineReplayDirtyChats, StringComparer.OrdinalIgnoreCase);
                    _offlineReplayPendingMessagesByChat.Clear();
                    _offlineReplayDirtyChats.Clear();
                    _offlineReplayPendingMessageCount = 0;
                    _lastOfflineReplayFlushUtc = DateTime.UtcNow;
                    _offlineReplayFlushTimer?.Dispose();
                    _offlineReplayFlushTimer = null;
                }

                try
                {
                    int saved = 0;
                    var outgoingIdsToRemove = new HashSet<string>(StringComparer.Ordinal);
                    var incomingIdsToRemove = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var kvp in snapshot)
                    {
                        if (kvp.Value == null || kvp.Value.Count == 0)
                        {
                            continue;
                        }

                        var batchMessages = kvp.Value
                            .Where(m => m != null)
                            .GroupBy(
                                m => string.IsNullOrWhiteSpace(m.Id) ? Guid.NewGuid().ToString() : m.Id,
                                StringComparer.Ordinal)
                            .Select(g => g.Last())
                            .OrderByDescending(m => m.Timestamp)
                            .Take(MaxPersistMessagesPerChatBatch)
                            .OrderBy(m => m.Timestamp)
                            .ToList();

                        await PersistLiveMessagesAsync(kvp.Key, batchMessages);

                        var outgoingIds = batchMessages
                            .Where(m => m != null && m.IsFromMe && !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();
                        var incomingIds = batchMessages
                            .Where(m => m != null && !m.IsFromMe && !string.IsNullOrWhiteSpace(m.Id))
                            .Select(m => m.Id)
                            .Distinct(StringComparer.Ordinal)
                            .ToList();

                        foreach (var outgoingId in outgoingIds)
                        {
                            outgoingIdsToRemove.Add(outgoingId);
                        }
                        foreach (var incomingId in incomingIds)
                        {
                            incomingIdsToRemove.Add(incomingId);
                        }

                        saved += batchMessages.Count;
                    }

                    if (outgoingIdsToRemove.Count > 0)
                    {
                        await _messageStore.RemovePendingOutgoingAsync(outgoingIdsToRemove);
                    }
                    if (incomingIdsToRemove.Count > 0)
                    {
                        await _messageStore.RemovePendingIncomingAsync(incomingIdsToRemove);
                    }

                    Debug.WriteLine($"[WhatsAppService] Flushed {saved} queued message(s) across {snapshot.Count} chat(s), dirtyChats={dirtyChats.Count}, reason={reason}");
                    if (!reason.StartsWith("shutdown", StringComparison.OrdinalIgnoreCase))
                    {
                        SchedulePersist();
                    }
                }
                catch (Exception ex)
                {
                    lock (_offlineReplayPersistLock)
                    {
                        foreach (var kvp in snapshot)
                        {
                            if (!_offlineReplayPendingMessagesByChat.TryGetValue(kvp.Key, out var pending))
                            {
                                pending = new List<ChatMessage>();
                                _offlineReplayPendingMessagesByChat[kvp.Key] = pending;
                            }

                            foreach (var message in kvp.Value.Where(m => m != null))
                            {
                                int existingIndex = !string.IsNullOrWhiteSpace(message.Id)
                                    ? pending.FindIndex(m => string.Equals(m?.Id, message.Id, StringComparison.Ordinal))
                                    : -1;
                                if (existingIndex >= 0)
                                {
                                    pending[existingIndex] = message;
                                }
                                else
                                {
                                    pending.Add(message);
                                    _offlineReplayPendingMessageCount++;
                                }
                            }
                        }

                        foreach (var jid in dirtyChats)
                        {
                            _offlineReplayDirtyChats.Add(jid);
                        }
                    }

                    RuntimeDiagnosticsService.Instance.RecordException(
                        "messages",
                        "message-batch-flush-deferred",
                        ex,
                        "reason=" + reason + "; chats=" + snapshot.Count);
                }
            }
            finally
            {
                bool scheduleAnother = false;
                lock (_offlineReplayPersistLock)
                {
                    _offlineReplayFlushRequested = false;
                    scheduleAnother = _offlineReplayPendingMessageCount > 0;
                    if (scheduleAnother)
                    {
                        ScheduleOfflineReplayFlushTimer_NoLock();
                    }
                }

                _offlineReplayFlushLock.Release();
            }
        }

        /// <summary>
        /// Persists current chats and messages to disk.
        /// </summary>

        public async Task PersistDataAsync()
        {
            await _persistRunLock.WaitAsync();
            try
            {
                // No OnSyncStatus here: mark-read and avatar/name enrichment used to surface
                // "Saving chats..." on the Mobile status bar for the whole catalog rewrite, which
                // looked like opening a chat was blocked on disk. Sync stages already report
                // through SyncPhaseStatus; this path is housekeeping.

                // Messages are persisted by the batched message queue. Rewriting every
                // loaded chat file here caused long UI stalls and large allocation spikes.
                List<ChatItem> chatSnapshot = null;
                List<string> chatJids = null;
                Dictionary<string, string> contactSnapshot = null;
                Dictionary<string, string> phoneContactSnapshot = null;
                Dictionary<string, string> aliasSnapshot = null;
                await RunOnUiThreadAsync(() =>
                {
                    chatSnapshot = Chats.Where(c => c != null).ToList();
                    chatJids = chatSnapshot
                        .Select(c => NormalizeJid(c.JID))
                        .Where(j => !string.IsNullOrWhiteSpace(j))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    contactSnapshot = new Dictionary<string, string>(ContactNames, StringComparer.OrdinalIgnoreCase);
                    phoneContactSnapshot = new Dictionary<string, string>(PhoneContactNamesByJid, StringComparer.OrdinalIgnoreCase);
                    aliasSnapshot = JidAlias.Snapshot();
                });

                await PersistChatCatalogAsync(chatSnapshot).ConfigureAwait(false);
                await _messageStore.SaveContactNamesAsync(contactSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());
                await _messageStore.SavePhoneContactNamesAsync(phoneContactSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());
                await _messageStore.SaveJidAliasesAsync(aliasSnapshot ?? new Dictionary<string, string>(), chatJids ?? new List<string>());

                Debug.WriteLine($"[WhatsAppService] Persisted {(chatSnapshot?.Count ?? 0)} chat rows and contact metadata");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Failed to persist data: {ex.Message}");
            }
            finally
            {
                _persistRunLock.Release();
            }
        }

        /// <summary>
        /// Writes only the given chat-list rows to <c>history_chat_preview</c>. Used after
        /// mark-read so clearing a badge does not rewrite the whole catalogue and contact maps.
        /// </summary>
        private async Task PersistChatCatalogSliceAsync(IList<ChatItem> chats)
        {
            if (chats == null || chats.Count == 0)
            {
                return;
            }

            await _persistRunLock.WaitAsync().ConfigureAwait(false);
            try
            {
                await PersistChatCatalogAsync(chats).ConfigureAwait(false);
                Debug.WriteLine("[WhatsAppService] Persisted chat-preview slice rows=" + chats.Count);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat-preview slice persist failed: " + ex.Message);
            }
            finally
            {
                _persistRunLock.Release();
            }
        }

        /// <summary>
        /// Schedules a debounced persist operation. Multiple calls within 3 seconds will batch into one save.
        /// </summary>
        private void SchedulePersist()
        {
            lock (_persistLock)
            {
                if (_suppressStartupScheduledPersist)
                {
                    _persistPending = true;
                    Debug.WriteLine("[WhatsAppService] SchedulePersist skipped during startup warm-up");
                    return;
                }

                _persistPending = true;
                
                // Cancel existing timer and restart with 3 second delay
                _persistTimer?.Dispose();
                _persistTimer = new System.Threading.Timer(async _ =>
                {
                    lock (_persistLock)
                    {
                        if (!_persistPending) return;
                        _persistPending = false;
                    }
                    
                    await PersistDataAsync();
                }, null, 3000, Timeout.Infinite);
            }
        }

        /// <summary>
        /// Public accessor for SchedulePersist - allows UI to trigger debounced save
        /// </summary>
        public void SchedulePersistPublic() => SchedulePersist();

        /// <inheritdoc />
        public void PersistChatListRowsPublic(IList<ChatItem> chats)
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

        private async Task PersistChatCatalogAsync(IList<ChatItem> chats)
        {
            if (_chatPreviews == null || chats == null)
            {
                return;
            }

            var rows = new List<HistoryChatPreview>(chats.Count);
            for (int i = 0; i < chats.Count; i++)
            {
                HistoryChatPreview row = HistoryChatPreviewApplier.FromChatItem(chats[i]);
                if (row != null)
                {
                    rows.Add(row);
                }
            }

            if (rows.Count == 0)
            {
                return;
            }

            await _chatPreviews.UpsertManyAsync(rows, notifyChunk: false).ConfigureAwait(false);
        }

        /// <summary>
        /// Loads persisted chats from disk on startup.
        /// </summary>
        private async Task LoadPersistedChatsAsync()
        {
            _isLoadingPersistedChats = true;
            try
            {
                IReadOnlyList<HistoryChatPreview> stored =
                    await _chatPreviews.GetAllAsync().ConfigureAwait(false);
                if (stored == null || stored.Count == 0)
                {
                    return;
                }

                await RunOnUiThreadAsync(() =>
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
                    SortChatsForDisplay();
                });

                Debug.WriteLine("[WhatsAppService] Fast startup loaded " + stored.Count + " chat preview rows");
                OnHistorySyncReceived?.Invoke(this, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Failed to load persisted chat metadata: " + ex.Message);
            }
            finally
            {
                _isLoadingPersistedChats = false;
            }
        }





        private async Task NormalizePersistedChatNamesAsync()
        {
            await RunOnUiThreadAsync(() =>
                {
                    int updated = 0;
                    foreach (var chat in Chats)
                    {
                        if (chat == null) continue;

                        string resolved = ResolveDisplayName(chat.JID, "chat");
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, chat.IsGroup);
                        bool resolvedMeaningful = IsMeaningfulChatLabel(resolved, chat.JID, chat.IsGroup);
                        bool shouldReplace = !string.IsNullOrEmpty(resolved) &&
                                             !string.Equals(chat.Name, resolved, StringComparison.Ordinal) &&
                                             (resolvedMeaningful || !existingMeaningful);

                        if (shouldReplace)
                        {
                            string oldName = chat.Name;
                            chat.Name = resolved;
                            updated++;
                            Debug.WriteLine($"[WhatsAppService] Normalized persisted chat title '{oldName}' -> '{resolved}' for {chat.JID}");
                        }
                    }

                    if (updated > 0)
                    {
                        Debug.WriteLine($"[WhatsAppService] Normalized {updated} persisted chat titles");
                        OnDisplayNamesUpdated?.Invoke(this, EventArgs.Empty);
                        SchedulePersist();
                    }
                });
        }

        private async Task PersistCriticalSuspendStateAsync()
        {
            List<ChatItem> chatSnapshot = null;
            List<string> chatJids = null;
            Dictionary<string, string> aliasSnapshot = null;
            await RunOnUiThreadAsync(() =>
            {
                chatSnapshot = Chats.Where(c => c != null).ToList();
                chatJids = chatSnapshot
                    .Select(c => NormalizeJid(c.JID))
                    .Where(j => !string.IsNullOrWhiteSpace(j))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                aliasSnapshot = JidAlias.Snapshot();
            });

            await PersistChatCatalogAsync(chatSnapshot ?? new List<ChatItem>()).ConfigureAwait(false);
            await _messageStore.SaveJidAliasesAsync(
                aliasSnapshot ?? new Dictionary<string, string>(),
                chatJids ?? new List<string>());

            RuntimeDiagnosticsService.Instance.Write(
                "lifecycle",
                "fast-suspend-persisted",
                "chatRows=" + (chatSnapshot?.Count ?? 0));
        }

        private async Task PersistSuspendTailAsync()
        {
            try
            {
                // Recent incoming messages are already durable in the append-only
                // journal. Rewriting large per-chat JSON files here can exceed the
                // Windows Phone suspend deadline and make the process look like a
                // crash. Only the compact chat-list/alias snapshot is best effort.
                await PersistCriticalSuspendStateAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "lifecycle",
                    "suspend-persist-tail-failed",
                    ex);
            }
        }
    }
}
