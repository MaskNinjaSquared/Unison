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
using Proto;
using Unison.UWPApp.Protocol;

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
        private const string TC_TOKENS_FOLDER = "tctokens";
        private const string APP_STATE_SYNC_KEYS_FOLDER = "app-state-sync-keys";
        private const string APP_STATE_SYNC_STATE_FOLDER = "app-state-sync-state";
        private const string ACCOUNT_FILE = "account.json";

        private StorageFolder _rootFolder;
        private StorageFolder _sessionsFolder;
        private StorageFolder _prekeysFolder;
        private StorageFolder _senderKeysFolder;
        private StorageFolder _tcTokensFolder;
        private StorageFolder _appStateSyncKeysFolder;
        private StorageFolder _appStateSyncStateFolder;

        // In-memory cache for performance
        private readonly ConcurrentDictionary<string, byte[]> _sessionCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<int, PreKeyData> _preKeyCache = new ConcurrentDictionary<int, PreKeyData>();
        private readonly ConcurrentDictionary<string, byte[]> _senderKeyCache = new ConcurrentDictionary<string, byte[]>();
        private readonly ConcurrentDictionary<string, TcTokenData> _tcTokenCache = new ConcurrentDictionary<string, TcTokenData>(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Message.Types.AppStateSyncKeyData> _appStateSyncKeyCache = new ConcurrentDictionary<string, Message.Types.AppStateSyncKeyData>(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, AppStateCollectionState> _appStateCollectionStateCache = new ConcurrentDictionary<string, AppStateCollectionState>(StringComparer.OrdinalIgnoreCase);
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
                _tcTokensFolder = await _rootFolder.CreateFolderAsync(TC_TOKENS_FOLDER, CreationCollisionOption.OpenIfExists);
                _appStateSyncKeysFolder = await _rootFolder.CreateFolderAsync(APP_STATE_SYNC_KEYS_FOLDER, CreationCollisionOption.OpenIfExists);
                _appStateSyncStateFolder = await _rootFolder.CreateFolderAsync(APP_STATE_SYNC_STATE_FOLDER, CreationCollisionOption.OpenIfExists);

                // Load existing data into cache
                await LoadSessionsIntoCacheAsync();
                await LoadPreKeysIntoCacheAsync();
                await LoadSenderKeysIntoCacheAsync();
                await LoadTcTokensIntoCacheAsync();
                await LoadAccountIntoCacheAsync();
                await LoadAppStateSyncKeysIntoCacheAsync();
                await LoadAppStateCollectionStateIntoCacheAsync();

                _initialized = true;
                Debug.WriteLine($"[KeyStore] Initialized. Sessions: {_sessionCache.Count}, PreKeys: {_preKeyCache.Count}, SenderKeys: {_senderKeyCache.Count}, TcTokens: {_tcTokenCache.Count}, AppStateKeys: {_appStateSyncKeyCache.Count}, AppStateCollections: {_appStateCollectionStateCache.Count}, Account: {_accountCache != null}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to initialize: {ex.Message}");
                throw;
            }
        }

        public async Task ClearAllAsync()
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var existingRoot = await localFolder.TryGetItemAsync(ROOT_FOLDER) as StorageFolder;
                if (existingRoot != null)
                {
                    await existingRoot.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    Debug.WriteLine("[KeyStore] Deleted SignalKeys root folder");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to delete SignalKeys root folder: {ex.Message}");
            }

            _sessionCache.Clear();
            _preKeyCache.Clear();
            _senderKeyCache.Clear();
            _tcTokenCache.Clear();
            _appStateSyncKeyCache.Clear();
            _appStateCollectionStateCache.Clear();
            _accountCache = null;
            _fileLocks.Clear();

            _rootFolder = null;
            _sessionsFolder = null;
            _prekeysFolder = null;
            _senderKeysFolder = null;
            _tcTokensFolder = null;
            _appStateSyncKeysFolder = null;
            _appStateSyncStateFolder = null;
            _initialized = false;

            await InitializeAsync();
            Debug.WriteLine("[KeyStore] Reinitialized after full clear");
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

        private static string BuildSenderKeySessionKey(string groupJid, string senderJid)
        {
            string normalizedSender = WA.NormalizeDeviceJid(senderJid);
            return $"sk:{groupJid}:{normalizedSender}";
        }

        public async Task<byte[]> GetSenderKeyAsync(string groupJid, string senderJid)
        {
            EnsureInitialized();

            string sessionKey = BuildSenderKeySessionKey(groupJid, senderJid);
            if (_senderKeyCache.TryGetValue(sessionKey, out var cached))
            {
                return cached;
            }

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
                if (data?.SenderKeyData == null)
                {
                    return null;
                }

                var bytes = Convert.FromBase64String(data.SenderKeyData);
                _senderKeyCache[sessionKey] = bytes;
                return bytes;
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

            string sessionKey = BuildSenderKeySessionKey(groupJid, senderJid);
            _senderKeyCache[sessionKey] = data;

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

        public Task<Dictionary<string, byte[]>> GetAllSenderKeysAsync()
        {
            EnsureInitialized();
            return Task.FromResult(new Dictionary<string, byte[]>(_senderKeyCache));
        }

        private async Task LoadSenderKeysIntoCacheAsync()
        {
            try
            {
                var files = await _senderKeysFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<SenderKeyFileData>(json);
                        if (data?.SenderKeyData != null &&
                            !string.IsNullOrWhiteSpace(data.GroupJid) &&
                            !string.IsNullOrWhiteSpace(data.SenderJid))
                        {
                            _senderKeyCache[BuildSenderKeySessionKey(data.GroupJid, data.SenderJid)] =
                                Convert.FromBase64String(data.SenderKeyData);
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load sender key file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate sender key files: {ex.Message}");
            }
        }

        #endregion

        #region Trusted Contact Tokens

        public async Task<TcTokenData> GetTcTokenAsync(string jid)
        {
            EnsureInitialized();

            string normalizedJid = WA.GetBaseJid(WA.NormalizeDeviceJid(jid));
            if (string.IsNullOrWhiteSpace(normalizedJid))
            {
                return null;
            }

            if (_tcTokenCache.TryGetValue(normalizedJid, out var cached))
            {
                return CloneTcTokenData(cached);
            }

            var fileLock = GetFileLock($"tctoken-{normalizedJid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(normalizedJid)}.json";
                var file = await _tcTokensFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<TcTokenFileData>(json);
                if (data == null)
                {
                    return null;
                }

                var token = new TcTokenData
                {
                    Token = string.IsNullOrEmpty(data.Token) ? null : Convert.FromBase64String(data.Token),
                    Timestamp = data.Timestamp,
                    SenderTimestamp = data.SenderTimestamp
                };
                _tcTokenCache[normalizedJid] = CloneTcTokenData(token);
                return token;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read tctoken for {normalizedJid}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetTcTokenAsync(string jid, TcTokenData data)
        {
            EnsureInitialized();

            string normalizedJid = WA.GetBaseJid(WA.NormalizeDeviceJid(jid));
            if (string.IsNullOrWhiteSpace(normalizedJid))
            {
                return;
            }

            if (data == null)
            {
                await RemoveTcTokenAsync(normalizedJid);
                return;
            }

            var clone = CloneTcTokenData(data) ?? new TcTokenData();
            _tcTokenCache[normalizedJid] = clone;

            var fileLock = GetFileLock($"tctoken-{normalizedJid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(normalizedJid)}.json";
                var file = await _tcTokensFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                var fileData = new TcTokenFileData
                {
                    Jid = normalizedJid,
                    Token = clone.Token == null || clone.Token.Length == 0 ? null : Convert.ToBase64String(clone.Token),
                    Timestamp = clone.Timestamp,
                    SenderTimestamp = clone.SenderTimestamp,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                var json = JsonConvert.SerializeObject(fileData, Formatting.Indented);
                await FileIO.WriteTextAsync(file, json);
                Debug.WriteLine($"[KeyStore] Saved tctoken for {normalizedJid} (hasToken={clone.Token != null && clone.Token.Length > 0}, ts={clone.Timestamp}, senderTs={clone.SenderTimestamp})");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save tctoken for {normalizedJid}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task RemoveTcTokenAsync(string jid)
        {
            EnsureInitialized();

            string normalizedJid = WA.GetBaseJid(WA.NormalizeDeviceJid(jid));
            if (string.IsNullOrWhiteSpace(normalizedJid))
            {
                return;
            }

            _tcTokenCache.TryRemove(normalizedJid, out _);

            var fileLock = GetFileLock($"tctoken-{normalizedJid}");
            await fileLock.WaitAsync();
            try
            {
                var fileName = $"{SanitizeFileName(normalizedJid)}.json";
                var file = await _tcTokensFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file != null)
                {
                    await file.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    Debug.WriteLine($"[KeyStore] Removed tctoken for {normalizedJid}");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to remove tctoken for {normalizedJid}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public Task<Dictionary<string, TcTokenData>> GetAllTcTokensAsync()
        {
            EnsureInitialized();
            return Task.FromResult(_tcTokenCache.ToDictionary(kvp => kvp.Key, kvp => CloneTcTokenData(kvp.Value), StringComparer.OrdinalIgnoreCase));
        }

        private async Task LoadTcTokensIntoCacheAsync()
        {
            try
            {
                var files = await _tcTokensFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<TcTokenFileData>(json);
                        if (!string.IsNullOrWhiteSpace(data?.Jid))
                        {
                            _tcTokenCache[WA.GetBaseJid(WA.NormalizeDeviceJid(data.Jid))] = new TcTokenData
                            {
                                Token = string.IsNullOrEmpty(data.Token) ? null : Convert.FromBase64String(data.Token),
                                Timestamp = data.Timestamp,
                                SenderTimestamp = data.SenderTimestamp
                            };
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load tctoken file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate tctoken files: {ex.Message}");
            }
        }

        private static TcTokenData CloneTcTokenData(TcTokenData data)
        {
            if (data == null)
            {
                return null;
            }

            return new TcTokenData
            {
                Token = data.Token?.ToArray(),
                Timestamp = data.Timestamp,
                SenderTimestamp = data.SenderTimestamp
            };
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

        #region App State Sync

        public async Task<Message.Types.AppStateSyncKeyData> GetAppStateSyncKeyAsync(string keyId)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(keyId))
            {
                return null;
            }

            if (_appStateSyncKeyCache.TryGetValue(keyId, out var cached))
            {
                return cached;
            }

            var fileLock = GetFileLock($"app-state-key-{keyId}");
            await fileLock.WaitAsync();
            try
            {
                var file = await _appStateSyncKeysFolder.TryGetItemAsync($"{SanitizeFileName(keyId)}.json") as StorageFile;
                if (file == null)
                {
                    return null;
                }

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<AppStateSyncKeyFileData>(json);
                if (data?.ProtoData == null)
                {
                    return null;
                }

                var parsed = Message.Types.AppStateSyncKeyData.Parser.ParseFrom(Convert.FromBase64String(data.ProtoData));
                _appStateSyncKeyCache[keyId] = parsed;
                return parsed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read app-state sync key {keyId}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetAppStateSyncKeyAsync(string keyId, Message.Types.AppStateSyncKeyData data)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(keyId) || data == null)
            {
                return;
            }

            _appStateSyncKeyCache[keyId] = data;

            var fileLock = GetFileLock($"app-state-key-{keyId}");
            await fileLock.WaitAsync();
            try
            {
                var file = await _appStateSyncKeysFolder.CreateFileAsync($"{SanitizeFileName(keyId)}.json", CreationCollisionOption.ReplaceExisting);
                byte[] protoBytes = new byte[data.CalculateSize()];
                using (var ms = new System.IO.MemoryStream(protoBytes))
                using (var cos = new Google.Protobuf.CodedOutputStream(ms))
                {
                    data.WriteTo(cos);
                    cos.Flush();
                }
                var fileData = new AppStateSyncKeyFileData
                {
                    KeyId = keyId,
                    ProtoData = Convert.ToBase64String(protoBytes),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(fileData, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save app-state sync key {keyId}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public Task<Dictionary<string, Message.Types.AppStateSyncKeyData>> GetAllAppStateSyncKeysAsync()
        {
            EnsureInitialized();
            return Task.FromResult(new Dictionary<string, Message.Types.AppStateSyncKeyData>(_appStateSyncKeyCache, StringComparer.Ordinal));
        }

        public async Task<AppStateCollectionState> GetAppStateCollectionStateAsync(string name)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            if (_appStateCollectionStateCache.TryGetValue(name, out var cached))
            {
                return CloneCollectionState(cached);
            }

            var fileLock = GetFileLock($"app-state-collection-{name}");
            await fileLock.WaitAsync();
            try
            {
                var file = await _appStateSyncStateFolder.TryGetItemAsync($"{SanitizeFileName(name)}.json") as StorageFile;
                if (file == null)
                {
                    return null;
                }

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<AppStateCollectionStateFileData>(json);
                if (data == null)
                {
                    return null;
                }

                var state = new AppStateCollectionState
                {
                    Name = data.Name,
                    Version = data.Version,
                    Hash = string.IsNullOrWhiteSpace(data.Hash) ? new byte[128] : Convert.FromBase64String(data.Hash),
                    IndexValueMap = (data.IndexValueMap ?? new Dictionary<string, string>(StringComparer.Ordinal))
                        .ToDictionary(kvp => kvp.Key, kvp => Convert.FromBase64String(kvp.Value), StringComparer.Ordinal)
                };

                _appStateCollectionStateCache[name] = state;
                return CloneCollectionState(state);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to read app-state collection state {name}: {ex.Message}");
                return null;
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task SetAppStateCollectionStateAsync(string name, AppStateCollectionState state)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(name) || state == null)
            {
                return;
            }

            var cloned = CloneCollectionState(state);
            cloned.Name = name;
            _appStateCollectionStateCache[name] = cloned;

            var fileLock = GetFileLock($"app-state-collection-{name}");
            await fileLock.WaitAsync();
            try
            {
                var file = await _appStateSyncStateFolder.CreateFileAsync($"{SanitizeFileName(name)}.json", CreationCollisionOption.ReplaceExisting);
                var fileData = new AppStateCollectionStateFileData
                {
                    Name = name,
                    Version = cloned.Version,
                    Hash = cloned.Hash != null ? Convert.ToBase64String(cloned.Hash) : null,
                    IndexValueMap = cloned.IndexValueMap?.ToDictionary(
                        kvp => kvp.Key,
                        kvp => Convert.ToBase64String(kvp.Value),
                        StringComparer.Ordinal),
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(fileData, Formatting.Indented));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to save app-state collection state {name}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task RemoveAppStateCollectionStateAsync(string name)
        {
            EnsureInitialized();

            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            _appStateCollectionStateCache.TryRemove(name, out _);

            var fileLock = GetFileLock($"app-state-collection-{name}");
            await fileLock.WaitAsync();
            try
            {
                var file = await _appStateSyncStateFolder.TryGetItemAsync($"{SanitizeFileName(name)}.json") as StorageFile;
                if (file != null)
                {
                    await file.DeleteAsync();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to remove app-state collection state {name}: {ex.Message}");
            }
            finally
            {
                fileLock.Release();
            }
        }

        private async Task LoadAppStateSyncKeysIntoCacheAsync()
        {
            try
            {
                var files = await _appStateSyncKeysFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<AppStateSyncKeyFileData>(json);
                        if (string.IsNullOrWhiteSpace(data?.KeyId) || string.IsNullOrWhiteSpace(data.ProtoData))
                        {
                            continue;
                        }

                        _appStateSyncKeyCache[data.KeyId] = Message.Types.AppStateSyncKeyData.Parser.ParseFrom(Convert.FromBase64String(data.ProtoData));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load app-state key file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate app-state key files: {ex.Message}");
            }
        }

        private async Task LoadAppStateCollectionStateIntoCacheAsync()
        {
            try
            {
                var files = await _appStateSyncStateFolder.GetFilesAsync();
                foreach (var file in files.Where(f => f.FileType == ".json"))
                {
                    try
                    {
                        var json = await FileIO.ReadTextAsync(file);
                        var data = JsonConvert.DeserializeObject<AppStateCollectionStateFileData>(json);
                        if (string.IsNullOrWhiteSpace(data?.Name))
                        {
                            continue;
                        }

                        _appStateCollectionStateCache[data.Name] = new AppStateCollectionState
                        {
                            Name = data.Name,
                            Version = data.Version,
                            Hash = string.IsNullOrWhiteSpace(data.Hash) ? new byte[128] : Convert.FromBase64String(data.Hash),
                            IndexValueMap = (data.IndexValueMap ?? new Dictionary<string, string>(StringComparer.Ordinal))
                                .ToDictionary(kvp => kvp.Key, kvp => Convert.FromBase64String(kvp.Value), StringComparer.Ordinal)
                        };
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[KeyStore] Failed to load app-state collection file {file.Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to enumerate app-state collection files: {ex.Message}");
            }
        }

        private static AppStateCollectionState CloneCollectionState(AppStateCollectionState state)
        {
            if (state == null)
            {
                return null;
            }

            return new AppStateCollectionState
            {
                Name = state.Name,
                Version = state.Version,
                Hash = state.Hash?.ToArray(),
                IndexValueMap = state.IndexValueMap?.ToDictionary(
                    kvp => kvp.Key,
                    kvp => kvp.Value?.ToArray(),
                    StringComparer.Ordinal) ?? new Dictionary<string, byte[]>(StringComparer.Ordinal)
            };
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

        private class TcTokenFileData
        {
            public string Jid { get; set; }
            public string Token { get; set; }
            public long? Timestamp { get; set; }
            public long? SenderTimestamp { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        private class AccountFileData
        {
            public string Details { get; set; }
            public string AccountSignatureKey { get; set; }
            public string AccountSignature { get; set; }
            public string DeviceSignature { get; set; }
        }

        private class AppStateSyncKeyFileData
        {
            public string KeyId { get; set; }
            public string ProtoData { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        private class AppStateCollectionStateFileData
        {
            public string Name { get; set; }
            public long Version { get; set; }
            public string Hash { get; set; }
            public Dictionary<string, string> IndexValueMap { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        #endregion
    }
}
