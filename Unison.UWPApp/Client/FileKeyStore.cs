using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Newtonsoft.Json;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// File-based implementation of IKeyStore using UWP LocalFolder.
    /// Stores sessions, pre-keys, sender-keys, and account info in JSON files.
    /// Based on Baileys' useMultiFileAuthState pattern.
    /// </summary>
    public class FileKeyStore : IKeyStore
    {
        private const string ROOT_FOLDER = "SignalKeys";
        private const string SESSIONS_FOLDER = "sessions";
        private const string PREKEYS_FOLDER = "prekeys";
        private const string SENDER_KEYS_FOLDER = "sender-keys";
        private const string ACCOUNT_FILE = "account.json";

        private StorageFolder _rootFolder;
        private StorageFolder _sessionsFolder;
        private StorageFolder _prekeysFolder;
        private StorageFolder _senderKeysFolder;

        // In-memory cache for performance
        private readonly ConcurrentDictionary<string, byte[]> _sessionCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<int, PreKeyData> _preKeyCache = new ConcurrentDictionary<int, PreKeyData>();
        private AccountInfo _accountCache;

        // File locks to prevent race conditions
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _fileLocks = new ConcurrentDictionary<string, SemaphoreSlim>();

        private bool _initialized = false;

        /// <summary>
        /// Initialize the key store - must be called before any other operation
        /// </summary>
        public async Task InitializeAsync()
        {
            if (_initialized) return;

            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                _rootFolder = await localFolder.CreateFolderAsync(ROOT_FOLDER, CreationCollisionOption.OpenIfExists);
                _sessionsFolder = await _rootFolder.CreateFolderAsync(SESSIONS_FOLDER, CreationCollisionOption.OpenIfExists);
                _prekeysFolder = await _rootFolder.CreateFolderAsync(PREKEYS_FOLDER, CreationCollisionOption.OpenIfExists);
                _senderKeysFolder = await _rootFolder.CreateFolderAsync(SENDER_KEYS_FOLDER, CreationCollisionOption.OpenIfExists);

                // Load existing data into cache
                await LoadSessionsIntoCacheAsync();
                await LoadPreKeysIntoCacheAsync();
                await LoadAccountIntoCacheAsync();

                _initialized = true;
                Debug.WriteLine($"[KeyStore] Initialized. Sessions: {_sessionCache.Count}, PreKeys: {_preKeyCache.Count}, Account: {_accountCache != null}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to initialize: {ex.Message}");
                throw;
            }
        }

        private SemaphoreSlim GetFileLock(string key)
        {
            return _fileLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        private string SanitizeFileName(string name)
        {
            // Replace characters invalid in filenames
            return name.Replace("/", "__").Replace(":", "-").Replace("@", "_at_");
        }

        #region Sessions

        public async Task<byte[]> GetSessionAsync(string jid)
        {
            EnsureInitialized();
            
            if (_sessionCache.TryGetValue(jid, out var cached))
                return cached;

            var fileLock = GetFileLock($"session-{jid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(jid)}.json";
                var file = await _sessionsFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<SessionFileData>(json);
                if (data?.SessionData != null)
                {
                    var bytes = Convert.FromBase64String(data.SessionData);
                    _sessionCache[jid] = bytes;
                    return bytes;
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read session for {jid}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetSessionAsync(string jid, byte[] data)
        {
            EnsureInitialized();

            _sessionCache[jid] = data;

            var fileLock = GetFileLock($"session-{jid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(jid)}.json";
                var file = await _sessionsFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var fileData = new SessionFileData
                {
                    Jid = jid,
                    SessionData = Convert.ToBase64String(data),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                var json = JsonConvert.SerializeObject(fileData, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
                Debug.WriteLine($"[KeyStore] Saved session for {jid}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save session for {jid}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task RemoveSessionAsync(string jid)
        {
            EnsureInitialized();

            _sessionCache.TryRemove(jid, out _);

            var fileLock = GetFileLock($"session-{jid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(jid)}.json";
                var file = await _sessionsFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file != null)
                {
                    await file.DeleteAsync();
                    Debug.WriteLine($"[KeyStore] Removed session for {jid}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to remove session for {jid}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public Task<IEnumerable<string>> GetAllSessionJidsAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_sessionCache.Keys.AsEnumerable());
        }

        public bool HasSession(string jid)
        {
            return _sessionCache.ContainsKey(jid);
        }

        private async Task LoadSessionsIntoCacheAsync()
        {
            try
            {
                var files = await _sessionsFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<SessionFileData>(json);
                        if (data?.SessionData != null && !string.IsNullOrEmpty(data.Jid))
                        {
                            _sessionCache[data.Jid] = Convert.FromBase64String(data.SessionData);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load session file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate session files: {ex.Message}");
            }
        }

        #endregion

        #region Pre-Keys

        public async Task<PreKeyData> GetPreKeyAsync(int id)
        {
            EnsureInitialized();

            if (_preKeyCache.TryGetValue(id, out var cached))
                return cached;

            var fileLock = GetFileLock($"prekey-{id}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{id}.json";
                var file = await _prekeysFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<PreKeyFileData>(json);
                if (data != null)
                {
                    var preKey = new PreKeyData
                    {
                        Id = data.Id,
                        KeyPair = new Crypto.KeyPair(
                            Convert.FromBase64String(data.PrivateKey),
                            Convert.FromBase64String(data.PublicKey)
                        )
                    };
                    _preKeyCache[id] = preKey;
                    return preKey;
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read pre-key {id}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetPreKeyAsync(int id, PreKeyData data)
        {
            EnsureInitialized();

            _preKeyCache[id] = data;

            var fileLock = GetFileLock($"prekey-{id}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{id}.json";
                var file = await _prekeysFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var fileData = new PreKeyFileData
                {
                    Id = data.Id,
                    PrivateKey = Convert.ToBase64String(data.KeyPair.Private),
                    PublicKey = Convert.ToBase64String(data.KeyPair.Public)
                };
                var json = JsonConvert.SerializeObject(fileData, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save pre-key {id}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task RemovePreKeyAsync(int id)
        {
            EnsureInitialized();

            _preKeyCache.TryRemove(id, out _);

            var fileLock = GetFileLock($"prekey-{id}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{id}.json";
                var file = await _prekeysFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file != null)
                {
                    await file.DeleteAsync();
                    Debug.WriteLine($"[KeyStore] Removed pre-key {id}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to remove pre-key {id}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public Task<Dictionary<int, PreKeyData>> GetAllPreKeysAsync()
        {
            EnsureInitialized();
            return Task.FromResult(new Dictionary<int, PreKeyData>(_preKeyCache));
        }

        private async Task LoadPreKeysIntoCacheAsync()
        {
            try
            {
                var files = await _prekeysFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<PreKeyFileData>(json);
                        if (data != null)
                        {
                            var preKey = new PreKeyData
                            {
                                Id = data.Id,
                                KeyPair = new Crypto.KeyPair(
                                    Convert.FromBase64String(data.PrivateKey),
                                    Convert.FromBase64String(data.PublicKey)
                                )
                            };
                            _preKeyCache[data.Id] = preKey;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load pre-key file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate pre-key files: {ex.Message}");
            }
        }

        #endregion

        #region Sender Keys

        public async Task<byte[]> GetSenderKeyAsync(string groupJid, string senderJid)
        {
            EnsureInitialized();

            var key = $"{groupJid}--{senderJid}";
            var fileLock = GetFileLock($"sender-key-{key}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(key)}.json";
                var file = await _senderKeysFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<SenderKeyFileData>(json);
                return data?.SenderKeyData != null ? Convert.FromBase64String(data.SenderKeyData) : null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read sender key for {key}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetSenderKeyAsync(string groupJid, string senderJid, byte[] data)
        {
            EnsureInitialized();

            var key = $"{groupJid}--{senderJid}";
            var fileLock = GetFileLock($"sender-key-{key}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(key)}.json";
                var file = await _senderKeysFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var fileData = new SenderKeyFileData
                {
                    GroupJid = groupJid,
                    SenderJid = senderJid,
                    SenderKeyData = Convert.ToBase64String(data),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                var json = JsonConvert.SerializeObject(fileData, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save sender key for {key}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        #endregion

        #region Account

        public async Task<AccountInfo> GetAccountAsync()
        {
            EnsureInitialized();

            if (_accountCache != null)
                return _accountCache;

            var fileLock = GetFileLock("account");
            await fileLock.WaitAsync();
            try
            {
                var file = await _rootFolder.TryGetItemAsync(ACCOUNT_FILE) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<AccountFileData>(json);
                if (data != null)
                {
                    _accountCache = new AccountInfo
                    {
                        Details = Convert.FromBase64String(data.Details),
                        AccountSignatureKey = Convert.FromBase64String(data.AccountSignatureKey),
                        AccountSignature = Convert.FromBase64String(data.AccountSignature),
                        DeviceSignature = Convert.FromBase64String(data.DeviceSignature)
                    };
                    return _accountCache;
                }
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read account: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetAccountAsync(AccountInfo account)
        {
            EnsureInitialized();

            _accountCache = account;

            var fileLock = GetFileLock("account");
            await fileLock.WaitAsync();
            try
            {
                var file = await _rootFolder.CreateFileAsync(ACCOUNT_FILE, CreationCollisionOption.ReplaceExisting);
                var fileData = new AccountFileData
                {
                    Details = Convert.ToBase64String(account.Details),
                    AccountSignatureKey = Convert.ToBase64String(account.AccountSignatureKey),
                    AccountSignature = Convert.ToBase64String(account.AccountSignature),
                    DeviceSignature = Convert.ToBase64String(account.DeviceSignature)
                };
                var json = JsonConvert.SerializeObject(fileData, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
                Debug.WriteLine("[KeyStore] Saved account info");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save account: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        private async Task LoadAccountIntoCacheAsync()
        {
            try
            {
                var file = await _rootFolder.TryGetItemAsync(ACCOUNT_FILE) as StorageFile;
                if (file == null) return;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<AccountFileData>(json);
                if (data != null)
                {
                    _accountCache = new AccountInfo
                    {
                        Details = Convert.FromBase64String(data.Details),
                        AccountSignatureKey = Convert.FromBase64String(data.AccountSignatureKey),
                        AccountSignature = Convert.FromBase64String(data.AccountSignature),
                        DeviceSignature = Convert.FromBase64String(data.DeviceSignature)
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to load account: {ex.Message}");
            }
        }

        #endregion

        private void EnsureInitialized()
        {
            if (!_initialized)
                throw new InvalidOperationException("KeyStore not initialized. Call InitializeAsync() first.");
        }

        #region File DTOs

        private class SessionFileData
        {
            public string Jid { get; set; }
            public string SessionData { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        private class PreKeyFileData
        {
            public int Id { get; set; }
            public string PrivateKey { get; set; }
            public string PublicKey { get; set; }
        }

        private class SenderKeyFileData
        {
            public string GroupJid { get; set; }
            public string SenderJid { get; set; }
            public string SenderKeyData { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        private class AccountFileData
        {
            public string Details { get; set; }
            public string AccountSignatureKey { get; set; }
            public string AccountSignature { get; set; }
            public string DeviceSignature { get; set; }
        }

        #endregion
    }
}
