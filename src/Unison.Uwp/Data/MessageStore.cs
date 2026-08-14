using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Newtonsoft.Json;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Uwp.Helpers;
using Unison.Uwp.Services;
using Unison.Uwp.Services.WhatsApp;

namespace Unison.Uwp.Data
{
    /// <summary>
    /// Persistent storage for messages and chat metadata.
    /// Uses JSON under LocalFolder/MessageStore/{syncId}/ (Messages/, Outbox/, chats.json).
    /// Wipe rotates <see cref="LocalSettingsConstants.MessageStoreSyncId"/> so resync
    /// never fights delete-in-place on the previous generation. Pre-epoch LocalFolder
    /// trees are deleted (not migrated); upgrade sets
    /// <see cref="LocalSettingsConstants.MessageStoreForceHistoryRepair"/>.
    /// </summary>
    public class MessageStore : IMessageStore
    {
        private const string STORES_CONTAINER = "MessageStore";
        private const string MESSAGES_FOLDER = "Messages";
        private const string OUTBOX_FOLDER = "Outbox";
        private const string INCOMING_JOURNAL_FILE = "incoming_pending.jsonl";
        private const string CHATS_FILE = "chats.json";
        private const string CHATS_BACKUP_FILE = "chats.bak.json";
        private const string CHATS_TEMP_FILE = "chats.tmp.json";
        private const string CONTACT_NAMES_FILE = "contact_names.json";
        private const string PHONE_CONTACT_NAMES_FILE = "phone_contact_names.json";
        private const string JID_ALIASES_FILE = "jid_aliases.json";
        private const string MESSAGE_BACKUP_SUFFIX = ".bak";
        private const string MESSAGE_TEMP_SUFFIX = ".tmp";
        // Era 50000. Um unico JSON com 50 mil mensagens era relido, mesclado, copiado
        // e reescrito INTEIRO a cada salvamento -- inviavel no armazenamento do Lumia.
        private const int MAX_MESSAGES_PER_CHAT = 1500;

        // Faz o backup completo apenas a cada N gravacoes do mesmo arquivo, em vez de
        // toda vez (o backup custava 1 leitura + 1 copia integral por mensagem).
        private const int BACKUP_EVERY_N_SAVES = 25;

        private static readonly string[] StoreRootFiles =
        {
            CHATS_FILE,
            CHATS_BACKUP_FILE,
            CHATS_TEMP_FILE,
            CONTACT_NAMES_FILE,
            PHONE_CONTACT_NAMES_FILE,
            JID_ALIASES_FILE,
            INCOMING_JOURNAL_FILE
        };

        private StorageFolder _appLocalFolder;
        private StorageFolder _storesContainer;
        private StorageFolder _storeRoot;
        private StorageFolder _messagesFolder;
        private StorageFolder _outboxFolder;
        private string _syncId;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _outboxLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _incomingJournalLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<PendingIncomingRecord> _incomingJournalQueue =
            new ConcurrentQueue<PendingIncomingRecord>();
        private readonly object _incomingJournalTimerLock = new object();
        private System.Threading.Timer _incomingJournalTimer;
        private static readonly TimeSpan IncomingJournalFlushDelay = TimeSpan.FromMilliseconds(150);
        private bool _initialized = false;

        // Cache em memoria das mensagens ja carregadas, por arquivo. Evita reparsear
        // o JSON inteiro do disco a cada mensagem recebida durante a sincronizacao.
        private readonly Dictionary<string, List<ChatMessage>> _messageCache =
            new Dictionary<string, List<ChatMessage>>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _saveCounters =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly object _cacheLock = new object();

        // Ordem de uso do cache, para descartar o mais antigo quando encher.
        // SEM esse limite o cache retinha as mensagens de TODOS os chats abertos na
        // memoria, estourando o limite do aparelho (1 GB) e fechando o app sozinho.
        private readonly List<string> _cacheOrder = new List<string>();
        private const int MAX_CACHED_CHATS = 2;

        /// <summary>
        /// Libera o cache de mensagens da memoria. Chamado pelo monitor de memoria
        /// quando o app se aproxima do limite do aparelho.
        /// </summary>
        public void ClearMemoryCache()
        {
            lock (_cacheLock)
            {
                _messageCache.Clear();
                _cacheOrder.Clear();
            }
        }

        private void TouchCache(string fileName, List<ChatMessage> messages)
        {
            lock (_cacheLock)
            {
                _messageCache[fileName] = messages;

                _cacheOrder.Remove(fileName);
                _cacheOrder.Add(fileName);

                while (_cacheOrder.Count > MAX_CACHED_CHATS)
                {
                    var oldest = _cacheOrder[0];
                    _cacheOrder.RemoveAt(0);
                    _messageCache.Remove(oldest);
                }
            }
        }

        /// <summary>
        /// Initialize the store and create necessary folders for the current sync epoch.
        /// Legacy LocalFolder/Messages is not migrated â€” abandoned + force history repair.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                await OpenOrCreateCurrentEpochAsync().ConfigureAwait(false);
                _initialized = true;
                WhatsAppService.Log(
                    $"[MessageStore] Initialized. syncId={_syncId} path={_messagesFolder.Path}");
                ScheduleOrphanEpochCleanup();
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to initialize: {ex.Message}");
                throw;
            }
        }

        private async Task OpenOrCreateCurrentEpochAsync()
        {
            _appLocalFolder = ApplicationData.Current.LocalFolder;
            _storesContainer = await _appLocalFolder.CreateFolderAsync(
                STORES_CONTAINER,
                CreationCollisionOption.OpenIfExists);

            string syncId = null;
            try
            {
                syncId = LocalSettingsAccess.Current.Get<string>(LocalSettingsConstants.MessageStoreSyncId);
            }
            catch
            {
                syncId = null;
            }

            bool createdNew = string.IsNullOrWhiteSpace(syncId);
            bool hadLegacy = createdNew && await HasLegacyRootArtifactsAsync().ConfigureAwait(false);

            if (createdNew)
            {
                syncId = Guid.NewGuid().ToString("N");
                LocalSettingsAccess.Current.Set(LocalSettingsConstants.MessageStoreSyncId, syncId);

                // Upgrade path: leave legacy JSON behind and refill from WhatsApp history.
                if (hadLegacy)
                {
                    MarkForceHistoryRepair("legacy-root-detected");
                }
            }

            await BindEpochFoldersAsync(syncId).ConfigureAwait(false);
        }

        private async Task BindEpochFoldersAsync(string syncId)
        {
            _syncId = syncId;
            _storeRoot = await _storesContainer.CreateFolderAsync(
                syncId,
                CreationCollisionOption.OpenIfExists);
            _messagesFolder = await _storeRoot.CreateFolderAsync(
                MESSAGES_FOLDER,
                CreationCollisionOption.OpenIfExists);
            _outboxFolder = await _storeRoot.CreateFolderAsync(
                OUTBOX_FOLDER,
                CreationCollisionOption.OpenIfExists);
        }

        private async Task<bool> HasLegacyRootArtifactsAsync()
        {
            if (_appLocalFolder == null)
            {
                return false;
            }

            if (await _appLocalFolder.TryGetItemAsync(MESSAGES_FOLDER) != null)
            {
                return true;
            }

            if (await _appLocalFolder.TryGetItemAsync(OUTBOX_FOLDER) != null)
            {
                return true;
            }

            foreach (string name in StoreRootFiles)
            {
                if (await _appLocalFolder.TryGetItemAsync(name) != null)
                {
                    return true;
                }
            }

            return false;
        }

        private static void MarkForceHistoryRepair(string reason)
        {
            try
            {
                LocalSettingsAccess.Current.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);
                LocalSettingsAccess.Current.Set(LocalSettingsConstants.LastFullHistoryRepairCompletedUtc, "");
                WhatsAppService.Log(
                    $"[MessageStore] Force history repair armed ({reason}); legacy store will be deleted, not migrated.");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to arm history repair: {ex.Message}");
            }
        }

        private void ScheduleOrphanEpochCleanup()
        {
            var container = _storesContainer;
            if (container == null)
            {
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var folders = await container.GetFoldersAsync();
                    foreach (var folder in folders)
                    {
                        string keepId = null;
                        try
                        {
                            keepId = LocalSettingsAccess.Current.Get<string>(
                                LocalSettingsConstants.MessageStoreSyncId);
                        }
                        catch
                        {
                            keepId = null;
                        }

                        if (string.IsNullOrEmpty(keepId) ||
                            string.Equals(folder.Name, keepId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        try
                        {
                            await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                            WhatsAppService.Log(
                                $"[MessageStore] Deleted orphan epoch folder: {folder.Name}");
                        }
                        catch (Exception ex)
                        {
                            WhatsAppService.Log(
                                $"[MessageStore] Orphan epoch delete deferred ({folder.Name}): {ex.Message}");
                        }
                    }

                    await DeleteLegacyRootArtifactsAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[MessageStore] Orphan cleanup failed: {ex.Message}");
                }
            });
        }

        private async Task DeleteLegacyRootArtifactsAsync()
        {
            if (_appLocalFolder == null)
            {
                return;
            }

            foreach (string folderName in new[] { MESSAGES_FOLDER, OUTBOX_FOLDER })
            {
                var folder = await _appLocalFolder.TryGetItemAsync(folderName) as StorageFolder;
                if (folder == null)
                {
                    continue;
                }

                try
                {
                    await folder.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    WhatsAppService.Log($"[MessageStore] Deleted legacy folder: {folderName}");
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log(
                        $"[MessageStore] Legacy folder delete deferred ({folderName}): {ex.Message}");
                }
            }

            foreach (string name in StoreRootFiles)
            {
                var file = await _appLocalFolder.TryGetItemAsync(name) as StorageFile;
                if (file == null)
                {
                    continue;
                }

                try
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    WhatsAppService.Log($"[MessageStore] Deleted legacy file: {name}");
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log(
                        $"[MessageStore] Legacy file delete deferred ({name}): {ex.Message}");
                }
            }
        }

        private sealed class PendingOutgoingEnvelope
        {
            public string ChatJid { get; set; }
            public ChatMessage Message { get; set; }
        }

        /// <summary>
        /// Queues incoming messages for a compact append-only journal. This write path
        /// is independent from the large per-chat JSON file, so a suspend or process
        /// termination cannot silently discard a recently decrypted message.
        /// </summary>
        public void QueuePendingIncoming(string chatJid, IEnumerable<ChatMessage> messages)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || messages == null)
            {
                return;
            }

            int queued = 0;
            foreach (var message in messages)
            {
                if (message == null || message.IsFromMe || string.IsNullOrWhiteSpace(message.Id))
                {
                    continue;
                }

                _incomingJournalQueue.Enqueue(new PendingIncomingRecord
                {
                    ChatJid = chatJid,
                    Message = message
                });
                queued++;
            }

            if (queued == 0)
            {
                return;
            }

            ScheduleIncomingJournalFlush();
        }

        private void ScheduleIncomingJournalFlush()
        {
            lock (_incomingJournalTimerLock)
            {
                if (_incomingJournalTimer == null)
                {
                    _incomingJournalTimer = new System.Threading.Timer(
                        _ =>
                        {
                            _ = FlushPendingIncomingJournalSafelyAsync();
                        },
                        null,
                        (int)IncomingJournalFlushDelay.TotalMilliseconds,
                        Timeout.Infinite);
                }
            }
        }

        private async Task FlushPendingIncomingJournalSafelyAsync()
        {
            try
            {
                await FlushPendingIncomingJournalAsync();
            }
            catch
            {
                // The append method already requeues and records the failure.
            }
        }

        public async Task FlushPendingIncomingJournalAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            lock (_incomingJournalTimerLock)
            {
                _incomingJournalTimer?.Dispose();
                _incomingJournalTimer = null;
            }

            var pending = new List<PendingIncomingRecord>();
            PendingIncomingRecord record;
            while (_incomingJournalQueue.TryDequeue(out record))
            {
                if (record?.Message != null &&
                    !string.IsNullOrWhiteSpace(record.ChatJid) &&
                    !string.IsNullOrWhiteSpace(record.Message.Id))
                {
                    pending.Add(record);
                }
            }

            if (pending.Count == 0)
            {
                return;
            }

            await _incomingJournalLock.WaitAsync();
            try
            {
                var builder = new System.Text.StringBuilder();
                foreach (var item in pending)
                {
                    builder.Append(JsonConvert.SerializeObject(item, Formatting.None));
                    builder.Append("\n");
                }

                var file = await _storeRoot.CreateFileAsync(
                    INCOMING_JOURNAL_FILE,
                    CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, builder.ToString());

                RuntimeDiagnosticsService.Instance.Write(
                    "messages",
                    "incoming-journal-appended",
                    "count=" + pending.Count);
            }
            catch (Exception ex)
            {
                foreach (var item in pending)
                {
                    _incomingJournalQueue.Enqueue(item);
                }
                ScheduleIncomingJournalFlush();

                RuntimeDiagnosticsService.Instance.RecordException(
                    "messages",
                    "incoming-journal-append-failed",
                    ex,
                    "count=" + pending.Count);
                throw;
            }
            finally
            {
                _incomingJournalLock.Release();
            }
        }

        public async Task<List<PendingIncomingRecord>> LoadPendingIncomingAsync()
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            await FlushPendingIncomingJournalAsync();
            await _incomingJournalLock.WaitAsync();
            try
            {
                var file = await _storeRoot.TryGetItemAsync(INCOMING_JOURNAL_FILE) as StorageFile;
                if (file == null)
                {
                    return new List<PendingIncomingRecord>();
                }

                var text = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new List<PendingIncomingRecord>();
                }

                var byMessage = new Dictionary<string, PendingIncomingRecord>(StringComparer.Ordinal);
                var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    try
                    {
                        var item = JsonConvert.DeserializeObject<PendingIncomingRecord>(line);
                        if (item?.Message == null ||
                            string.IsNullOrWhiteSpace(item.ChatJid) ||
                            string.IsNullOrWhiteSpace(item.Message.Id))
                        {
                            continue;
                        }

                        byMessage[item.ChatJid + "\u001f" + item.Message.Id] = item;
                    }
                    catch
                    {
                        // Keep reading after a partially-written final line.
                    }
                }

                return byMessage.Values.ToList();
            }
            finally
            {
                _incomingJournalLock.Release();
            }
        }

        public async Task RemovePendingIncomingAsync(IEnumerable<string> messageIds)
        {
            if (!_initialized)
            {
                await InitializeAsync();
            }

            var ids = new HashSet<string>(
                (messageIds ?? Enumerable.Empty<string>())
                    .Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            if (ids.Count == 0)
            {
                return;
            }

            await FlushPendingIncomingJournalAsync();
            await _incomingJournalLock.WaitAsync();
            try
            {
                var file = await _storeRoot.TryGetItemAsync(INCOMING_JOURNAL_FILE) as StorageFile;
                if (file == null)
                {
                    return;
                }

                var text = await FileIO.ReadTextAsync(file);
                var kept = new List<string>();
                var lines = (text ?? string.Empty).Split(
                    new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    try
                    {
                        var item = JsonConvert.DeserializeObject<PendingIncomingRecord>(line);
                        if (item?.Message == null ||
                            string.IsNullOrWhiteSpace(item.Message.Id) ||
                            ids.Contains(item.Message.Id))
                        {
                            continue;
                        }

                        kept.Add(JsonConvert.SerializeObject(item, Formatting.None));
                    }
                    catch
                    {
                        // Drop damaged journal lines during compaction.
                    }
                }

                if (kept.Count == 0)
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }
                else
                {
                    await FileIO.WriteTextAsync(file, string.Join("\n", kept) + "\n");
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "messages",
                    "incoming-journal-compacted",
                    "removed=" + ids.Count + "; remaining=" + kept.Count);
            }
            finally
            {
                _incomingJournalLock.Release();
            }
        }

        /// <summary>
        /// Stores one outgoing message in a tiny per-message file. This is the durable
        /// outbox used between the optimistic UI update and the normal batched chat-file
        /// flush, avoiding a full 1,500-message JSON rewrite on every tap of Send.
        /// </summary>
        public async Task SavePendingOutgoingAsync(string chatJid, ChatMessage message)
        {
            if (!_initialized) await InitializeAsync();
            if (string.IsNullOrWhiteSpace(chatJid) || message == null || string.IsNullOrWhiteSpace(message.Id)) return;

            await _outboxLock.WaitAsync();
            try
            {
                var envelope = new PendingOutgoingEnvelope
                {
                    ChatJid = chatJid,
                    Message = message
                };
                var json = await SerializeJsonAsync(envelope);
                var file = await _outboxFolder.CreateFileAsync(
                    SanitizeFileName(message.Id) + ".json",
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save outgoing outbox item {message?.Id}: {ex.Message}");
            }
            finally
            {
                _outboxLock.Release();
            }
        }

        public async Task<List<ChatMessage>> LoadPendingOutgoingForChatAsync(string chatJid)
        {
            if (!_initialized) await InitializeAsync();
            var result = new List<ChatMessage>();
            if (string.IsNullOrWhiteSpace(chatJid)) return result;

            await _outboxLock.WaitAsync();
            try
            {
                var files = await _outboxFolder.GetFilesAsync();
                foreach (var file in files)
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var envelope = await DeserializeJsonAsync<PendingOutgoingEnvelope>(json);
                        if (envelope?.Message != null &&
                            string.Equals(envelope.ChatJid, chatJid, StringComparison.OrdinalIgnoreCase))
                        {
                            envelope.Message.EnsureKindFromLegacyFlags();
                            result.Add(envelope.Message);
                        }
                    }
                    catch
                    {
                        // A single damaged outbox item must not hide the rest of the chat.
                    }
                }
            }
            finally
            {
                _outboxLock.Release();
            }

            return result;
        }

        public async Task<bool> AreMessagesPersistedAsync(string chatJid, IEnumerable<string> messageIds)
        {
            if (!_initialized) await InitializeAsync();
            var ids = new HashSet<string>(
                (messageIds ?? Enumerable.Empty<string>()).Where(id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
            if (ids.Count == 0) return true;

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var diskMessages = await TryLoadMessagesFileAsync(fileName);
                if (diskMessages == null) return false;

                var storedIds = new HashSet<string>(
                    diskMessages.Where(m => m != null && !string.IsNullOrWhiteSpace(m.Id)).Select(m => m.Id),
                    StringComparer.Ordinal);
                return ids.All(storedIds.Contains);
            }
            catch
            {
                return false;
            }
        }

        public async Task RemovePendingOutgoingAsync(IEnumerable<string> messageIds)
        {
            if (!_initialized) await InitializeAsync();
            var ids = (messageIds ?? Enumerable.Empty<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (ids.Count == 0) return;

            await _outboxLock.WaitAsync();
            try
            {
                foreach (var id in ids)
                {
                    try
                    {
                        var item = await _outboxFolder.TryGetItemAsync(SanitizeFileName(id) + ".json");
                        if (item is StorageFile file)
                        {
                            await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        }
                    }
                    catch
                    {
                        // Best-effort cleanup; duplicate IDs are upserted in the main store.
                    }
                }
            }
            finally
            {
                _outboxLock.Release();
            }
        }

        /// <summary>
        /// Saves a single message to the chat's message file.
        /// Appends to existing messages, enforcing MAX_MESSAGES_PER_CHAT limit.
        /// </summary>
        public async Task SaveMessageAsync(string chatJid, ChatMessage message)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var messages = new List<ChatMessage>(await LoadMessagesInternalAsync(fileName));
                
                // Upsert by message id so late media hydration can persist.
                int existingIndex = messages.FindIndex(m => m.Id == message.Id);
                if (existingIndex >= 0)
                {
                    messages[existingIndex] = MergeMessagePreferEnrichment(messages[existingIndex], message);
                    await SaveMessagesInternalAsync(fileName, messages);
                    WhatsAppService.Log($"[MessageStore] Updated message {message.Id} for {chatJid}");
                    return;
                }

                // Insert new message
                if (!messages.Any(m => m.Id == message.Id))
                {
                    messages.Add(message);
                    
                    // Enforce limit - keep most recent messages
                    if (messages.Count > MAX_MESSAGES_PER_CHAT)
                    {
                        messages = messages.OrderByDescending(m => m.Timestamp)
                                           .Take(MAX_MESSAGES_PER_CHAT)
                                           .OrderBy(m => m.Timestamp)
                                           .ToList();
                    }
                    
                    await SaveMessagesInternalAsync(fileName, messages);
                    WhatsAppService.Log($"[MessageStore] Saved {messages.Count} total messages for {chatJid}");
                }
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save message: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Saves multiple messages at once (batch operation for history sync).
        /// </summary>
        public async Task SaveMessagesAsync(string chatJid, IEnumerable<ChatMessage> newMessages)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var existingMessages = new List<ChatMessage>(await LoadMessagesInternalAsync(fileName));
                var indexById = new Dictionary<string, int>(StringComparer.Ordinal);
                for (int i = 0; i < existingMessages.Count; i++)
                {
                    var existingId = existingMessages[i]?.Id;
                    if (!string.IsNullOrWhiteSpace(existingId))
                    {
                        indexById[existingId] = i;
                    }
                }

                // Upsert em lote. Alem de novas mensagens, isso persiste hidratacao de
                // imagem/status sem reescrever o arquivo uma vez por item.
                foreach (var msg in newMessages ?? Enumerable.Empty<ChatMessage>())
                {
                    if (msg == null)
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(msg.Id) && indexById.TryGetValue(msg.Id, out var existingIndex))
                    {
                        existingMessages[existingIndex] = MergeMessagePreferEnrichment(
                            existingMessages[existingIndex],
                            msg);
                    }
                    else
                    {
                        existingMessages.Add(msg);
                        if (!string.IsNullOrWhiteSpace(msg.Id))
                        {
                            indexById[msg.Id] = existingMessages.Count - 1;
                        }
                    }
                }
                
                // Enforce limit
                if (existingMessages.Count > MAX_MESSAGES_PER_CHAT)
                {
                    existingMessages = existingMessages.OrderByDescending(m => m.Timestamp)
                                                       .Take(MAX_MESSAGES_PER_CHAT)
                                                       .OrderBy(m => m.Timestamp)
                                                       .ToList();
                }
                
                await SaveMessagesInternalAsync(fileName, existingMessages);
                WhatsAppService.Log($"[MessageStore] Saved {existingMessages.Count} total messages for {chatJid}");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save messages batch: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public async Task DeleteMessageAsync(string chatJid, string messageId)
        {
            if (!_initialized) await InitializeAsync();
            if (string.IsNullOrWhiteSpace(chatJid) || string.IsNullOrWhiteSpace(messageId))
            {
                return;
            }

            await _writeLock.WaitAsync();
            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var messages = new List<ChatMessage>(await LoadMessagesInternalAsync(fileName));
                int removed = messages.RemoveAll(m => string.Equals(m?.Id, messageId, StringComparison.Ordinal));
                if (removed > 0)
                {
                    await SaveMessagesInternalAsync(fileName, messages);
                    WhatsAppService.Log($"[MessageStore] Removed {removed} instance(s) of message {messageId} from {chatJid}");
                }
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to delete message {messageId} from {chatJid}: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Loads only a specific page of messages for a chat.
        /// </summary>
        public async Task<List<ChatMessage>> LoadMessagesPagedAsync(string chatJid, int skip, int take)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var allMessages = await LoadMessagesInternalAsync(fileName);

                // Os dados do cache ja sao normalizados em ordem cronologica. Evita
                // OrderBy().ToList() a cada pagina, que duplicava toda a conversa em RAM.
                int safeSkip = Math.Max(0, skip);
                int safeTake = Math.Max(0, take);
                var segment = allMessages.Skip(safeSkip).Take(safeTake).ToList();
                WhatsAppService.Log($"[MessageStore] Loaded page of {segment.Count} messages (skip={safeSkip}, take={safeTake}, total={allMessages.Count}) for {chatJid}");
                return segment;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load paged messages: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        /// <summary>
        /// Loads active pinned messages. The underlying message list is cache-backed, so
        /// opening a chat does not parse the same JSON twice.
        /// </summary>
        public async Task<List<ChatMessage>> LoadPinnedMessagesAsync(string chatJid, int maxCount = 3)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var allMessages = await LoadMessagesInternalAsync(fileName);
                DateTime nowUtc = DateTime.UtcNow;
                return allMessages
                    .Where(m => m != null && m.IsPinned &&
                                (!m.PinExpiresAtUtc.HasValue || m.PinExpiresAtUtc.Value > nowUtc))
                    .OrderByDescending(m => m.PinnedAtUtc ?? DateTime.MinValue)
                    .Take(Math.Max(1, maxCount))
                    .ToList();
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load pinned messages: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        public async Task<ChatMessage> FindMessageByIdAsync(string chatJid, string messageId)
        {
            if (string.IsNullOrWhiteSpace(messageId)) return null;
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var allMessages = await LoadMessagesInternalAsync(fileName);
                return allMessages.FirstOrDefault(m =>
                    string.Equals(m?.Id, messageId, StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to find message {messageId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Loads all messages for a chat.
        /// </summary>
        public async Task<List<ChatMessage>> LoadMessagesAsync(string chatJid)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var messages = await LoadMessagesInternalAsync(fileName);
                WhatsAppService.Log($"[MessageStore] Loaded {messages.Count} messages for {chatJid}");
                return new List<ChatMessage>(messages);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load messages: {ex.Message}");
                return new List<ChatMessage>();
            }
        }

        /// <summary>
        /// Saves the chat list metadata.
        /// </summary>
        public async Task SaveChatsAsync(IEnumerable<ChatItem> chats)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                WhatsAppService.Log($"[MessageStore] Saving chats to: {_storeRoot.Path}\\{CHATS_FILE}");
                var chatList = chats?.ToList() ?? new List<ChatItem>();
                var json = await SerializeJsonAsync(chatList);
                WhatsAppService.Log($"[MessageStore] Persisting {chatList.Count} chats to disk...");
                
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                var tempFile = await _storeRoot.CreateFileAsync(CHATS_TEMP_FILE, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(tempFile, bytes);

                var tempProps = await tempFile.GetBasicPropertiesAsync();
                if (tempProps.Size == 0)
                {
                    throw new IOException("Temporary chats file write produced 0 bytes");
                }

                var currentMain = await _storeRoot.TryGetItemAsync(CHATS_FILE) as StorageFile;
                if (currentMain != null)
                {
                    var currentProps = await currentMain.GetBasicPropertiesAsync();
                    if (currentProps.Size > 0)
                    {
                        await currentMain.CopyAsync(_storeRoot, CHATS_BACKUP_FILE, NameCollisionOption.ReplaceExisting);
                    }
                }

                await tempFile.CopyAsync(_storeRoot, CHATS_FILE, NameCollisionOption.ReplaceExisting);
                await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);

                var savedFile = await _storeRoot.GetFileAsync(CHATS_FILE);
                var savedProps = await savedFile.GetBasicPropertiesAsync();
                WhatsAppService.Log($"[MessageStore] Saved {chatList.Count} chats successfully ({savedProps.Size} bytes written to {savedFile.Path})");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save chats: {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Loads the chat list metadata.
        /// </summary>
        public async Task<List<ChatItem>> LoadChatsAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                WhatsAppService.Log($"[MessageStore] Looking for chats file in: {_storeRoot.Path}");

                var primaryChats = await TryReadChatsFromFileAsync(CHATS_FILE, "primary");
                if (primaryChats != null)
                {
                    return primaryChats;
                }

                var backupChats = await TryReadChatsFromFileAsync(CHATS_BACKUP_FILE, "backup");
                if (backupChats != null)
                {
                    WhatsAppService.Log($"[MessageStore] Recovered {backupChats.Count} chats from backup file");
                    try
                    {
                        var backupFile = await _storeRoot.GetFileAsync(CHATS_BACKUP_FILE);
                        await backupFile.CopyAsync(_storeRoot, CHATS_FILE, NameCollisionOption.ReplaceExisting);
                    }
                    catch (Exception exRestore)
                    {
                        WhatsAppService.Log($"[MessageStore] Failed to restore primary chats file from backup: {exRestore.Message}");
                    }
                    return backupChats;
                }

                WhatsAppService.Log("[MessageStore] No valid chats file found (primary and backup unavailable/invalid)");
                return new List<ChatItem>();
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load chats: {ex.GetType().Name}: {ex.Message}");
                return new List<ChatItem>();
            }
        }

        public async Task<List<ChatItem>> LoadChatsBackupAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var backupChats = await TryReadChatsFromFileAsync(CHATS_BACKUP_FILE, "backup-explicit");
                return backupChats ?? new List<ChatItem>();
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load backup chats explicitly: {ex.GetType().Name}: {ex.Message}");
                return new List<ChatItem>();
            }
        }

        public async Task<List<ChatItem>> RecoverChatsFromMessageFilesAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var files = await _messagesFolder.GetFilesAsync();
                var recovered = new List<ChatItem>();

                foreach (var file in files)
                {
                    if (!file.Name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string jid = Path.GetFileNameWithoutExtension(file.Name);
                    if (string.IsNullOrWhiteSpace(jid) || !jid.Contains("@"))
                    {
                        continue;
                    }

                    List<ChatMessage> messages;
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        if (string.IsNullOrWhiteSpace(json))
                        {
                            continue;
                        }
                        messages = await DeserializeJsonAsync<List<ChatMessage>>(json) ?? new List<ChatMessage>();
                    }
                    catch
                    {
                        continue;
                    }

                    if (messages.Count == 0)
                    {
                        continue;
                    }

                    var latest = messages
                        .Where(m => m != null)
                        .OrderByDescending(m => m.Timestamp)
                        .FirstOrDefault();
                    if (latest == null)
                    {
                        continue;
                    }

                    bool isGroup = JidHelper.IsGroupJid(jid);
                    string author = ChatPreviewNormalizer.FormatListAuthorPrefix(
                        latest,
                        isGroup,
                        LocalizedStrings.Get("Chat_SelfFallbackName", "You"));
                    string preview = ChatPreviewNormalizer.FormatListPreview(latest, isGroup);
                    if (string.IsNullOrWhiteSpace(preview))
                    {
                        preview = latest.IsImage ? "[Image]" : "[Media]";
                    }

                    Unison.Core.Helpers.ChatPreviewNormalizer.Normalize(
                        preview,
                        Unison.Core.Helpers.ChatPreviewNormalizer.InferKindFromMessage(latest),
                        out var kind,
                        out var cleanPreview);

                    recovered.Add(new ChatItem
                    {
                        JID = jid,
                        Name = jid.Split('@')[0],
                        LastMessage = cleanPreview,
                        LastMessageAuthor = author,
                        LastMessageKind = kind,
                        Timestamp = latest.Timestamp.ToString("g"),
                        LastMessageTimestampUtc = latest.Timestamp.Kind == DateTimeKind.Utc
                            ? latest.Timestamp
                            : latest.Timestamp.ToUniversalTime(),
                        Kind = isGroup ? ChatKind.Group : ChatKind.Direct
                    });
                }

                recovered = recovered
                    .GroupBy(c => c.JID, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                WhatsAppService.Log($"[MessageStore] Recovered {recovered.Count} chats from message files");
                return recovered;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed recovering chats from message files: {ex.Message}");
                return new List<ChatItem>();
            }
        }

        /// <summary>
        /// Gets the count of stored messages. O formato legado e um array JSON; portanto
        /// contar e paginar exigem a mesma leitura. Carregamos uma unica vez e servimos a
        /// pagina seguinte do cache, evitando duas desserializacoes por abertura/scroll.
        /// </summary>
        public async Task<int> GetMessageCountAsync(string chatJid)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var messages = await LoadMessagesInternalAsync(fileName);
                return messages?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Deletes all stored messages for a chat.
        /// </summary>
        public async Task DeleteChatMessagesAsync(string chatJid)
        {
            // Invalida o cache em memoria deste chat, senao dados apagados
            // continuariam sendo servidos da memoria.
            var cacheKey = SanitizeFileName(chatJid) + ".json";
            lock (_cacheLock)
            {
                _messageCache.Remove(cacheKey);
                _cacheOrder.Remove(cacheKey);
                _saveCounters.Remove(cacheKey);
            }

            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var file = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file != null)
                {
                    await file.DeleteAsync();
                    WhatsAppService.Log($"[MessageStore] Deleted messages for {chatJid}");
                }

                var backupFile = await _messagesFolder.TryGetItemAsync(fileName + MESSAGE_BACKUP_SUFFIX) as StorageFile;
                if (backupFile != null)
                {
                    await backupFile.DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to delete messages: {ex.Message}");
            }
        }

        /// <summary>
        /// Rotates the store epoch: new syncId folder becomes active immediately;
        /// the previous tree is deleted in the background (no in-place file deletes).
        /// </summary>
        public Task WipeAllDataAsync()
        {
            return RotateEpochAsync(preserveIdentitySidecars: false, reason: "epoch-wipe");
        }

        /// <summary>
        /// Rotates the store epoch clearing chats/messages/outbox, but copies contact-name
        /// and JID-alias sidecars into the new epoch (session/auth stays elsewhere).
        /// </summary>
        public Task WipeChatsAndMessagesAsync()
        {
            return RotateEpochAsync(preserveIdentitySidecars: true, reason: "chats-messages-wipe");
        }

        private async Task RotateEpochAsync(bool preserveIdentitySidecars, string reason)
        {
            lock (_cacheLock)
            {
                _messageCache.Clear();
                _cacheOrder.Clear();
                _saveCounters.Clear();
            }

            // Drop pending journal so it cannot flush into the new epoch.
            while (_incomingJournalQueue.TryDequeue(out _))
            {
            }

            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            StorageFolder oldRoot = null;
            string oldSyncId = null;
            try
            {
                oldRoot = _storeRoot;
                oldSyncId = _syncId;

                string newSyncId = Guid.NewGuid().ToString("N");
                // Persist before bind so concurrent orphan cleanup never deletes the new epoch.
                LocalSettingsAccess.Current.Set(
                    LocalSettingsConstants.MessageStoreSyncId,
                    newSyncId);
                await BindEpochFoldersAsync(newSyncId).ConfigureAwait(false);

                if (preserveIdentitySidecars && oldRoot != null)
                {
                    await CopyIdentitySidecarsAsync(oldRoot, _storeRoot).ConfigureAwait(false);
                }

                WhatsAppService.Log(
                    $"[MessageStore] Epoch rotated {oldSyncId} -> {newSyncId} ({reason}); resync can write immediately.");
                MarkForceHistoryRepair(reason);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to rotate epoch ({reason}): {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }

            if (oldRoot != null &&
                !string.Equals(oldRoot.Path, _storeRoot?.Path, StringComparison.OrdinalIgnoreCase))
            {
                var toDelete = oldRoot;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await toDelete.DeleteAsync(StorageDeleteOption.PermanentDelete);
                        WhatsAppService.Log(
                            $"[MessageStore] Deleted previous epoch folder: {toDelete.Name}");
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log(
                            $"[MessageStore] Previous epoch delete deferred ({toDelete.Name}): {ex.Message}");
                    }

                    await DeleteLegacyRootArtifactsAsync().ConfigureAwait(false);
                });
            }
        }

        private static async Task CopyIdentitySidecarsAsync(StorageFolder from, StorageFolder to)
        {
            if (from == null || to == null)
            {
                return;
            }

            string[] files =
            {
                CONTACT_NAMES_FILE,
                PHONE_CONTACT_NAMES_FILE,
                JID_ALIASES_FILE
            };

            foreach (string name in files)
            {
                try
                {
                    var item = await from.TryGetItemAsync(name);
                    var file = item as StorageFile;
                    if (file == null)
                    {
                        continue;
                    }

                    await file.CopyAsync(to, name, NameCollisionOption.ReplaceExisting);
                    WhatsAppService.Log($"[MessageStore] Copied identity sidecar into new epoch: {name}");
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log(
                        $"[MessageStore] Failed to copy identity sidecar {name}: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Saves contact names for JIDs that have chats.
        /// Only saves names for JIDs present in chatJids set.
        /// </summary>
        public async Task SaveContactNamesAsync(Dictionary<string, string> allContactNames, IEnumerable<string> chatJids)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                // Filter to only save names for JIDs that have chats
                var chatJidSet = new HashSet<string>(chatJids);
                var filteredNames = allContactNames
                    .Where(kvp => chatJidSet.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var file = await _storeRoot.CreateFileAsync(CONTACT_NAMES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = await SerializeJsonAsync(filteredNames);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await FileIO.WriteBytesAsync(file, bytes);
                WhatsAppService.Log($"[MessageStore] Saved {filteredNames.Count} contact names (filtered from {allContactNames.Count})");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save contact names: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Loads persisted contact names.
        /// </summary>
        public async Task<Dictionary<string, string>> LoadContactNamesAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var file = await _storeRoot.TryGetItemAsync(CONTACT_NAMES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No contact names file found");
                    return new Dictionary<string, string>();
                }

                var json = await FileIO.ReadTextAsync(file);
                var names = await DeserializeJsonAsync<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                WhatsAppService.Log($"[MessageStore] Loaded {names.Count} contact names");
                return names;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load contact names: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Saves local phone contact overlay names for JIDs that have chats.
        /// </summary>
        public async Task SavePhoneContactNamesAsync(Dictionary<string, string> allPhoneNames, IEnumerable<string> chatJids)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                var chatJidSet = new HashSet<string>(chatJids);
                var filteredNames = allPhoneNames
                    .Where(kvp => chatJidSet.Contains(kvp.Key))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

                var file = await _storeRoot.CreateFileAsync(PHONE_CONTACT_NAMES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = await SerializeJsonAsync(filteredNames);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await FileIO.WriteBytesAsync(file, bytes);
                WhatsAppService.Log($"[MessageStore] Saved {filteredNames.Count} phone contact names (filtered from {allPhoneNames.Count})");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save phone contact names: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Loads persisted local phone contact overlay names.
        /// </summary>
        public async Task<Dictionary<string, string>> LoadPhoneContactNamesAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var file = await _storeRoot.TryGetItemAsync(PHONE_CONTACT_NAMES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No phone contact names file found");
                    return new Dictionary<string, string>();
                }

                var json = await FileIO.ReadTextAsync(file);
                var names = await DeserializeJsonAsync<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                WhatsAppService.Log($"[MessageStore] Loaded {names.Count} phone contact names");
                return names;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load phone contact names: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Saves PN/LID alias mappings relevant to the current chat list so restart-time canonicalization can recover.
        /// </summary>
        public async Task SaveJidAliasesAsync(Dictionary<string, string> allAliases, IEnumerable<string> chatJids)
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                var chatJidSet = new HashSet<string>(
                    (chatJids ?? Enumerable.Empty<string>())
                        .Where(j => !string.IsNullOrWhiteSpace(j)),
                    StringComparer.OrdinalIgnoreCase);

                var filteredAliases = (allAliases ?? new Dictionary<string, string>())
                    .Where(kvp =>
                        !string.IsNullOrWhiteSpace(kvp.Key) &&
                        !string.IsNullOrWhiteSpace(kvp.Value) &&
                        (chatJidSet.Contains(kvp.Key) || chatJidSet.Contains(kvp.Value)))
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);

                var file = await _storeRoot.CreateFileAsync(JID_ALIASES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = await SerializeJsonAsync(filteredAliases);
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await FileIO.WriteBytesAsync(file, bytes);
                WhatsAppService.Log($"[MessageStore] Saved {filteredAliases.Count} JID aliases (filtered from {allAliases?.Count ?? 0})");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to save JID aliases: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
            }
        }

        /// <summary>
        /// Loads persisted PN/LID alias mappings.
        /// </summary>
        public async Task<Dictionary<string, string>> LoadJidAliasesAsync()
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var file = await _storeRoot.TryGetItemAsync(JID_ALIASES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No JID alias file found");
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var json = await FileIO.ReadTextAsync(file);
                var aliases = await DeserializeJsonAsync<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                WhatsAppService.Log($"[MessageStore] Loaded {aliases.Count} JID aliases");
                return new Dictionary<string, string>(aliases, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load JID aliases: {ex.Message}");
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        #region Private Helpers

        /// <summary>
        /// Le as mensagens servindo do cache em memoria quando possivel.
        /// Antes, cada mensagem recebida reparseava o JSON inteiro do disco --
        /// principal causa do travamento durante a sincronizacao de historico.
        /// </summary>
        private async Task<List<ChatMessage>> LoadMessagesInternalAsync(string fileName)
        {
            lock (_cacheLock)
            {
                if (_messageCache.TryGetValue(fileName, out var cached))
                {
                    _cacheOrder.Remove(fileName);
                    _cacheOrder.Add(fileName);
                    return cached;
                }
            }

            var loaded = NormalizeMessagesForStorage(await LoadMessagesFromDiskAsync(fileName));
            TouchCache(fileName, loaded);
            return loaded;
        }

        private async Task<List<ChatMessage>> LoadMessagesFromDiskAsync(string fileName)
        {
            try
            {
                bool primaryHasData = await MessageFileHasDataAsync(fileName);
                var primary = await TryLoadMessagesFileAsync(fileName);
                var backup = await TryLoadMessagesFileAsync(fileName + MESSAGE_BACKUP_SUFFIX);

                if (primary == null)
                {
                    if (backup != null)
                    {
                        WhatsAppService.Log($"[MessageStore] Primary message file unreadable for {fileName}; using backup with {backup.Count} messages");
                        return backup;
                    }

                    if (primaryHasData)
                    {
                        throw new IOException($"Message file {fileName} exists but could not be read");
                    }

                    return new List<ChatMessage>();
                }

                if (primary.Count == 0)
                {
                    return backup ?? new List<ChatMessage>();
                }

                if (backup != null && ShouldMergeMessageBackup(primary, backup))
                {
                    var merged = MergeMessages(primary, backup);
                    WhatsAppService.Log($"[MessageStore] Recovered {merged.Count - primary.Count} message(s) from backup for {fileName}");
                    return merged;
                }

                return primary;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load messages from {fileName}: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        private async Task SaveMessagesInternalAsync(string fileName, List<ChatMessage> messages)
        {
            messages = NormalizeMessagesForStorage(messages);

            // O bloco de backup abaixo custa 1 leitura integral + 1 copia integral do
            // arquivo. Executa-lo a CADA mensagem inviabilizava a sincronizacao, entao
            // agora roda apenas periodicamente (a protecao contra corrupcao continua,
            // so que amortizada).
            int contador;
            lock (_cacheLock)
            {
                _saveCounters.TryGetValue(fileName, out contador);
                contador++;
                _saveCounters[fileName] = contador;
            }
            bool fazerBackup = (contador % BACKUP_EVERY_N_SAVES) == 1;

            List<ChatMessage> backupMessages = null;
            var currentFile = fazerBackup
                ? await _messagesFolder.TryGetItemAsync(fileName) as StorageFile
                : null;
            if (fazerBackup)
            {
                backupMessages = await TryLoadMessagesFileAsync(fileName + MESSAGE_BACKUP_SUFFIX);
            }
            if (currentFile != null)
            {
                var props = await currentFile.GetBasicPropertiesAsync();
                if (props.Size > 0)
                {
                    var currentMessages = await TryLoadMessagesFileAsync(fileName);
                    if (currentMessages != null &&
                        currentMessages.Count > 0 &&
                        (backupMessages == null || currentMessages.Count >= backupMessages.Count))
                    {
                        await currentFile.CopyAsync(_messagesFolder, fileName + MESSAGE_BACKUP_SUFFIX, NameCollisionOption.ReplaceExisting);
                    }
                    else if (backupMessages != null && backupMessages.Count > 0)
                    {
                        WhatsAppService.Log($"[MessageStore] Preserved larger message backup for {fileName}: current={currentMessages?.Count ?? -1}, backup={backupMessages.Count}");
                    }
                }
            }

            if (backupMessages != null && backupMessages.Count > messages.Count + 10)
            {
                int before = messages.Count;
                messages = NormalizeMessagesForStorage(MergeMessages(messages, backupMessages));
                WhatsAppService.Log($"[MessageStore] Save protected by backup merge for {fileName}: before={before}, backup={backupMessages.Count}, merged={messages.Count}");
            }

            // O cache deve apontar para a versao final, inclusive depois de uma
            // recuperacao pelo backup.
            TouchCache(fileName, messages);

            var json = await SerializeJsonAsync(messages);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            var tempFile = await _messagesFolder.CreateFileAsync(fileName + MESSAGE_TEMP_SUFFIX, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteBytesAsync(tempFile, bytes);

            var tempProps = await tempFile.GetBasicPropertiesAsync();
            if (tempProps.Size == 0 && messages.Count > 0)
            {
                throw new IOException("Temporary message file write produced 0 bytes");
            }

            await tempFile.CopyAsync(_messagesFolder, fileName, NameCollisionOption.ReplaceExisting);
            await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
        }

        private static List<ChatMessage> NormalizeMessagesForStorage(IEnumerable<ChatMessage> messages)
        {
            if (messages == null)
            {
                return new List<ChatMessage>();
            }

            // Mantem a ordem estavel e o teto de armazenamento sem criar uma
            // segunda lista em cada leitura paginada.
            var normalized = messages
                .Where(m => m != null && !IsLegacyReactionRow(m))
                .OrderBy(m => m.Timestamp)
                .ThenBy(m => m.Id ?? string.Empty, StringComparer.Ordinal)
                .ToList();

            foreach (var message in normalized)
            {
                message.EnsureKindFromLegacyFlags();
            }

            if (normalized.Count > MAX_MESSAGES_PER_CHAT)
            {
                normalized.RemoveRange(0, normalized.Count - MAX_MESSAGES_PER_CHAT);
            }

            return normalized;
        }

        private static bool IsLegacyReactionRow(ChatMessage message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.Content))
            {
                return false;
            }

            return message.Content.StartsWith("[Reaction]", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<List<ChatMessage>> TryLoadMessagesFileAsync(string fileName)
        {
            try
            {
                var file = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null)
                {
                    return null;
                }

                var json = await FileIO.ReadTextAsync(file);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return new List<ChatMessage>();
                }

                return await DeserializeJsonAsync<List<ChatMessage>>(json) ?? new List<ChatMessage>();
            }
            catch
            {
                return null;
            }
        }

        private async Task<bool> MessageFileHasDataAsync(string fileName)
        {
            try
            {
                var file = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null)
                {
                    return false;
                }

                var props = await file.GetBasicPropertiesAsync();
                return props.Size > 0;
            }
            catch
            {
                return true;
            }
        }

        private static bool ShouldMergeMessageBackup(List<ChatMessage> primary, List<ChatMessage> backup)
        {
            if (backup == null || backup.Count == 0 || primary == null)
            {
                return false;
            }

            return backup.Count > primary.Count + 10 || primary.Count == 0;
        }

        private static List<ChatMessage> MergeMessages(List<ChatMessage> primary, List<ChatMessage> backup)
        {
            var merged = new Dictionary<string, ChatMessage>(StringComparer.Ordinal);
            foreach (var message in backup.Concat(primary))
            {
                if (message == null)
                {
                    continue;
                }

                var key = !string.IsNullOrWhiteSpace(message.Id)
                    ? message.Id
                    : $"{message.Timestamp.Ticks}:{message.IsFromMe}:{message.SenderName}:{message.Content}";
                if (merged.TryGetValue(key, out var existing))
                {
                    merged[key] = MergeMessagePreferEnrichment(existing, message);
                }
                else
                {
                    merged[key] = message;
                }
            }

            return merged.Values
                .OrderBy(m => m.Timestamp)
                .ThenBy(m => m.Id)
                .ToList();
        }

        /// <summary>
        /// Prefer incoming content/media, but keep a stronger participant/sender when
        /// a later upsert arrives without group author metadata.
        /// </summary>
        private static ChatMessage MergeMessagePreferEnrichment(ChatMessage existing, ChatMessage incoming)
        {
            if (existing == null) return incoming;
            if (incoming == null) return existing;

            if (string.IsNullOrWhiteSpace(incoming.ParticipantJid) &&
                !string.IsNullOrWhiteSpace(existing.ParticipantJid))
            {
                incoming.ParticipantJid = existing.ParticipantJid;
            }

            if (string.IsNullOrWhiteSpace(incoming.SenderName) &&
                !string.IsNullOrWhiteSpace(existing.SenderName))
            {
                incoming.SenderName = existing.SenderName;
            }

            return incoming;
        }

        private async Task<List<ChatItem>> TryReadChatsFromFileAsync(string fileName, string label)
        {
            var file = await _storeRoot.TryGetItemAsync(fileName) as StorageFile;
            if (file == null)
            {
                WhatsAppService.Log($"[MessageStore] No {label} chats file found ({fileName})");
                return null;
            }

            var props = await file.GetBasicPropertiesAsync();
            WhatsAppService.Log($"[MessageStore] Found {label} chats file: {file.Path}, size: {props.Size} bytes");
            if (props.Size == 0)
            {
                WhatsAppService.Log($"[MessageStore] {label} chats file is 0 bytes");
                return null;
            }

            var json = await FileIO.ReadTextAsync(file);
            WhatsAppService.Log($"[MessageStore] Read {json.Length} characters from {label} chats file");
            if (string.IsNullOrWhiteSpace(json))
            {
                WhatsAppService.Log($"[MessageStore] {label} chats file content is empty");
                return null;
            }

            var chats = await DeserializeJsonAsync<List<ChatItem>>(json);
            if (chats == null)
            {
                WhatsAppService.Log($"[MessageStore] {label} chats file JSON deserialize returned null");
                return null;
            }

            WhatsAppService.Log($"[MessageStore] Loaded {chats.Count} chats from {label} file");
            return chats;
        }

        /// <summary>
        /// JSON serialization is CPU-bound. Running it off the UI thread prevents
        /// long message histories from freezing navigation and input on Windows Mobile.
        /// </summary>
        private static Task<string> SerializeJsonAsync<T>(T value)
        {
            return Task.Run(() => JsonConvert.SerializeObject(value, Formatting.None));
        }

        private static Task<T> DeserializeJsonAsync<T>(string json)
        {
            return Task.Run(() => JsonConvert.DeserializeObject<T>(json));
        }

        private string SanitizeFileName(string input)
        {
            // Replace invalid filename characters
            var invalid = new HashSet<char>(Path.GetInvalidFileNameChars());
            return new string(input.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }

        #endregion
    }
}

