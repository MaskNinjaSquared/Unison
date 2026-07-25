using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Newtonsoft.Json;
using Unison.UWPApp.Models;
using Unison.UWPApp.Services;

namespace Unison.UWPApp.Data
{
    /// <summary>
    /// Persistent storage for messages and chat metadata.
    /// Uses JSON files in LocalFolder/Messages/ and LocalFolder/chats.json.
    /// </summary>
    public class MessageStore
    {
        private const string MESSAGES_FOLDER = "Messages";
        private const string CHATS_FILE = "chats.json";
        private const string CHATS_BACKUP_FILE = "chats.bak.json";
        private const string CHATS_TEMP_FILE = "chats.tmp.json";
        private const string CONTACT_NAMES_FILE = "contact_names.json";
        private const string PHONE_CONTACT_NAMES_FILE = "phone_contact_names.json";
        private const string JID_ALIASES_FILE = "jid_aliases.json";
        private const string MESSAGE_BACKUP_SUFFIX = ".bak";
        private const string MESSAGE_TEMP_SUFFIX = ".tmp";
        private const int MAX_MESSAGES_PER_CHAT = 50000;

        private StorageFolder _messagesFolder;
        private StorageFolder _localFolder;
        private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
        private bool _initialized = false;

        /// <summary>
        /// Initialize the store and create necessary folders.
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                _localFolder = ApplicationData.Current.LocalFolder;
                _messagesFolder = await _localFolder.CreateFolderAsync(MESSAGES_FOLDER, CreationCollisionOption.OpenIfExists);
                _initialized = true;
                WhatsAppService.Log($"[MessageStore] Initialized. Messages folder: {_messagesFolder.Path}");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to initialize: {ex.Message}");
                throw;
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
                var messages = await LoadMessagesInternalAsync(fileName);
                
                // Upsert by message id so late media hydration can persist.
                int existingIndex = messages.FindIndex(m => m.Id == message.Id);
                if (existingIndex >= 0)
                {
                    messages[existingIndex] = message;
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
                var existingMessages = await LoadMessagesInternalAsync(fileName);
                var existingIds = new HashSet<string>(existingMessages.Select(m => m.Id));
                
                // Add only new messages
                foreach (var msg in newMessages)
                {
                    if (!existingIds.Contains(msg.Id))
                    {
                        existingMessages.Add(msg);
                        existingIds.Add(msg.Id);
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
                var messages = await LoadMessagesInternalAsync(fileName);
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
                
                // Sort by timestamp and ID for stability
                var sorted = allMessages.OrderBy(m => m.Timestamp).ThenBy(m => m.Id).ToList();
                
                var segment = sorted.Skip(skip).Take(take).ToList();
                WhatsAppService.Log($"[MessageStore] Loaded page of {segment.Count} messages (skip={skip}, take={take}, total={allMessages.Count}) for {chatJid}");
                return segment;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to load paged messages: {ex.Message}");
                return new List<ChatMessage>();
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
                return messages;
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
                WhatsAppService.Log($"[MessageStore] Saving chats to: {_localFolder.Path}\\{CHATS_FILE}");
                var chatList = chats?.ToList() ?? new List<ChatItem>();
                var json = JsonConvert.SerializeObject(chatList, Formatting.Indented);
                WhatsAppService.Log($"[MessageStore] Persisting {chatList.Count} chats to disk...");
                
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                var tempFile = await _localFolder.CreateFileAsync(CHATS_TEMP_FILE, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteBytesAsync(tempFile, bytes);

                var tempProps = await tempFile.GetBasicPropertiesAsync();
                if (tempProps.Size == 0)
                {
                    throw new IOException("Temporary chats file write produced 0 bytes");
                }

                var currentMain = await _localFolder.TryGetItemAsync(CHATS_FILE) as StorageFile;
                if (currentMain != null)
                {
                    var currentProps = await currentMain.GetBasicPropertiesAsync();
                    if (currentProps.Size > 0)
                    {
                        await currentMain.CopyAsync(_localFolder, CHATS_BACKUP_FILE, NameCollisionOption.ReplaceExisting);
                    }
                }

                await tempFile.CopyAsync(_localFolder, CHATS_FILE, NameCollisionOption.ReplaceExisting);
                await tempFile.DeleteAsync(StorageDeleteOption.PermanentDelete);

                var savedFile = await _localFolder.GetFileAsync(CHATS_FILE);
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
                WhatsAppService.Log($"[MessageStore] Looking for chats file in: {_localFolder.Path}");

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
                        var backupFile = await _localFolder.GetFileAsync(CHATS_BACKUP_FILE);
                        await backupFile.CopyAsync(_localFolder, CHATS_FILE, NameCollisionOption.ReplaceExisting);
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
                        messages = JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new List<ChatMessage>();
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

                    string preview = latest.Content;
                    if (string.IsNullOrWhiteSpace(preview))
                    {
                        preview = latest.IsImage ? "[Image]" : "[Media]";
                    }
                    preview = preview.Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ");
                    if (preview.Length > 50)
                    {
                        preview = preview.Substring(0, 50) + "...";
                    }

                    recovered.Add(new ChatItem
                    {
                        JID = jid,
                        Name = jid.Split('@')[0],
                        LastMessage = preview,
                        Timestamp = latest.Timestamp.ToString("g"),
                        IsGroup = jid.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase)
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
        /// Gets the count of stored messages for a chat without loading them all.
        /// </summary>
        public async Task<int> GetMessageCountAsync(string chatJid)
        {
            if (!_initialized) await InitializeAsync();

            try
            {
                var fileName = SanitizeFileName(chatJid) + ".json";
                var file = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return 0;

                var json = await FileIO.ReadTextAsync(file);
                var messages = JsonConvert.DeserializeObject<List<ChatMessage>>(json);
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
        /// Deletes all stored messages, chats, and contact names.
        /// </summary>
        public async Task WipeAllDataAsync()
        {
            if (!_initialized) await InitializeAsync();

            await _writeLock.WaitAsync();
            try
            {
                // Delete all files in the messages folder
                var files = await _messagesFolder.GetFilesAsync();
                foreach (var file in files)
                {
                    await file.DeleteAsync();
                }
                WhatsAppService.Log("[MessageStore] Deleted all message files");

                // Delete chats and contact names files
                var chatsFile = await _localFolder.TryGetItemAsync(CHATS_FILE) as StorageFile;
                if (chatsFile != null) await chatsFile.DeleteAsync();

                var contactsFile = await _localFolder.TryGetItemAsync(CONTACT_NAMES_FILE) as StorageFile;
                if (contactsFile != null) await contactsFile.DeleteAsync();

                var phoneContactsFile = await _localFolder.TryGetItemAsync(PHONE_CONTACT_NAMES_FILE) as StorageFile;
                if (phoneContactsFile != null) await phoneContactsFile.DeleteAsync();

                WhatsAppService.Log("[MessageStore] Wiped all local data");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[MessageStore] Failed to wipe data: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
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

                var file = await _localFolder.CreateFileAsync(CONTACT_NAMES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(filteredNames, Formatting.Indented);
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
                var file = await _localFolder.TryGetItemAsync(CONTACT_NAMES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No contact names file found");
                    return new Dictionary<string, string>();
                }

                var json = await FileIO.ReadTextAsync(file);
                var names = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
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

                var file = await _localFolder.CreateFileAsync(PHONE_CONTACT_NAMES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(filteredNames, Formatting.Indented);
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
                var file = await _localFolder.TryGetItemAsync(PHONE_CONTACT_NAMES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No phone contact names file found");
                    return new Dictionary<string, string>();
                }

                var json = await FileIO.ReadTextAsync(file);
                var names = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
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

                var file = await _localFolder.CreateFileAsync(JID_ALIASES_FILE, CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(filteredAliases, Formatting.Indented);
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
                var file = await _localFolder.TryGetItemAsync(JID_ALIASES_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log("[MessageStore] No JID alias file found");
                    return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var json = await FileIO.ReadTextAsync(file);
                var aliases = JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
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

        private async Task<List<ChatMessage>> LoadMessagesInternalAsync(string fileName)
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
            var currentFile = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
            var backupMessages = await TryLoadMessagesFileAsync(fileName + MESSAGE_BACKUP_SUFFIX);
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
                messages = MergeMessages(messages, backupMessages);
                WhatsAppService.Log($"[MessageStore] Save protected by backup merge for {fileName}: before={before}, backup={backupMessages.Count}, merged={messages.Count}");
            }

            var json = JsonConvert.SerializeObject(messages, Formatting.Indented);
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

                return JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new List<ChatMessage>();
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
                merged[key] = message;
            }

            return merged.Values
                .OrderBy(m => m.Timestamp)
                .ThenBy(m => m.Id)
                .ToList();
        }

        private async Task<List<ChatItem>> TryReadChatsFromFileAsync(string fileName, string label)
        {
            var file = await _localFolder.TryGetItemAsync(fileName) as StorageFile;
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

            var chats = JsonConvert.DeserializeObject<List<ChatItem>>(json);
            if (chats == null)
            {
                WhatsAppService.Log($"[MessageStore] {label} chats file JSON deserialize returned null");
                return null;
            }

            WhatsAppService.Log($"[MessageStore] Loaded {chats.Count} chats from {label} file");
            return chats;
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

