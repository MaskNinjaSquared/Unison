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
        private const string CONTACT_NAMES_FILE = "contact_names.json";
        private const int MAX_MESSAGES_PER_CHAT = 1000;

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
                Debug.WriteLine($"[MessageStore] Initialized. Messages folder: {_messagesFolder.Path}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to initialize: {ex.Message}");
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
                
                // Check if message already exists (by Id)
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
                Debug.WriteLine($"[MessageStore] Failed to save message: {ex.Message}");
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
                Debug.WriteLine($"[MessageStore] Saved {existingMessages.Count} total messages for {chatJid}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to save messages batch: {ex.Message}");
            }
            finally
            {
                _writeLock.Release();
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
                Debug.WriteLine($"[MessageStore] Failed to load messages: {ex.Message}");
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
                var file = await _localFolder.CreateFileAsync(CHATS_FILE, CreationCollisionOption.ReplaceExisting);
                var json = JsonConvert.SerializeObject(chats.ToList(), Formatting.Indented);
                WhatsAppService.Log($"[MessageStore] Persisting {chats.Count()} chats to disk...");
                
                // Use stream-based write to bypass WinRT encoding issues
                var bytes = System.Text.Encoding.UTF8.GetBytes(json);
                await FileIO.WriteBytesAsync(file, bytes);
                
                // Verify the write
                var props = await file.GetBasicPropertiesAsync();
                WhatsAppService.Log($"[MessageStore] Saved {chats.Count()} chats successfully ({props.Size} bytes written to {file.Path})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to save chats: {ex.GetType().Name}: {ex.Message}");
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
                var file = await _localFolder.TryGetItemAsync(CHATS_FILE) as StorageFile;
                if (file == null)
                {
                    WhatsAppService.Log($"[MessageStore] No chats file found (file does not exist: {CHATS_FILE})");
                    return new List<ChatItem>();
                }

                var props = await file.GetBasicPropertiesAsync();
                WhatsAppService.Log($"[MessageStore] Found chats file: {file.Path}, size: {props.Size} bytes");

                var json = await FileIO.ReadTextAsync(file);
                WhatsAppService.Log($"[MessageStore] Read {json.Length} characters from chats file");
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    WhatsAppService.Log("[MessageStore] Chats file is empty");
                    return new List<ChatItem>();
                }

                var chats = JsonConvert.DeserializeObject<List<ChatItem>>(json) ?? new List<ChatItem>();
                WhatsAppService.Log($"[MessageStore] Loaded {chats.Count} chats from disk");
                return chats;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to load chats: {ex.GetType().Name}: {ex.Message}");
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
                    Debug.WriteLine($"[MessageStore] Deleted messages for {chatJid}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to delete messages: {ex.Message}");
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
                Debug.WriteLine($"[MessageStore] Saved {filteredNames.Count} contact names (filtered from {allContactNames.Count})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to save contact names: {ex.Message}");
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
                    Debug.WriteLine("[MessageStore] No contact names file found");
                    return new Dictionary<string, string>();
                }

                var json = await FileIO.ReadTextAsync(file);
                var names = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
                Debug.WriteLine($"[MessageStore] Loaded {names.Count} contact names");
                return names;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MessageStore] Failed to load contact names: {ex.Message}");
                return new Dictionary<string, string>();
            }
        }

        #region Private Helpers

        private async Task<List<ChatMessage>> LoadMessagesInternalAsync(string fileName)
        {
            try
            {
                var file = await _messagesFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return new List<ChatMessage>();

                var json = await FileIO.ReadTextAsync(file);
                return JsonConvert.DeserializeObject<List<ChatMessage>>(json) ?? new List<ChatMessage>();
            }
            catch
            {
                return new List<ChatMessage>();
            }
        }

        private async Task SaveMessagesInternalAsync(string fileName, List<ChatMessage> messages)
        {
            var file = await _messagesFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            var json = JsonConvert.SerializeObject(messages, Formatting.Indented);
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            await FileIO.WriteBytesAsync(file, bytes);
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
