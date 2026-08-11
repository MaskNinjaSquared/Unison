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
using Unison.Baileys.Protocol;
using Unison.Baileys.Crypto;
using Unison.Uwp.Services;

using Unison.Baileys.Client;

namespace Unison.Uwp.Client
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
        private const string PREKEYS_CACHE_FILE = "prekeys-cache.json";
        private const string PREKEYS_DELTA_FILE = "prekeys-delta.jsonl";
        private const string TC_TOKENS_CACHE_FILE = "tctokens-cache.json";
        private const string TC_TOKENS_DELTA_FILE = "tctokens-delta.jsonl";

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

        // Cold startup is dominated by dozens of small Signal key files. Initialize
        // critical keys first, then warm secondary caches while the network handshake
        // is already running. A bounded batch avoids flooding Lumia storage.
        private const int KEY_LOAD_BATCH_SIZE = 4;
        private readonly SemaphoreSlim _initializeLock = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _preKeySnapshotLock = new SemaphoreSlim(1, 1);
        private readonly object _preKeySnapshotTimerLock = new object();
        private System.Threading.Timer _preKeySnapshotTimer;
        private readonly SemaphoreSlim _tcTokenSnapshotLock = new SemaphoreSlim(1, 1);
        private readonly object _tcTokenSnapshotTimerLock = new object();
        private System.Threading.Timer _tcTokenSnapshotTimer;
        private readonly ConcurrentQueue<TcTokenDeltaRecord> _tcTokenDeltaQueue = new ConcurrentQueue<TcTokenDeltaRecord>();
        private readonly object _tcTokenDeltaTimerLock = new object();
        private System.Threading.Timer _tcTokenDeltaTimer;
        private readonly object _secondaryWarmupLock = new object();
        private Task _secondaryWarmupTask;
        private bool _criticalInitialized = false;
        private bool _fullInitialized = false;
        private bool _initialized = false;

        /// <summary>
        /// Initializes all key caches. Most callers can use this compatibility path;
        /// SocketClient uses InitializeCriticalAsync so the network can start earlier.
        /// </summary>
        public async Task InitializeAsync()
        {
            await InitializeCriticalAsync();
            await WarmSecondaryCachesAsync();
        }

        /// <summary>
        /// Loads only state required to start a registered Noise/Signal session:
        /// sessions, pre-keys and account. Sender keys, trusted-contact tokens and
        /// app-state data are available on demand and are warmed in the background.
        /// </summary>
        public async Task InitializeCriticalAsync()
        {
            if (_criticalInitialized)
            {
                return;
            }

            await _initializeLock.WaitAsync();
            try
            {
                if (_criticalInitialized)
                {
                    return;
                }

                var started = Stopwatch.StartNew();
                var localFolder = ApplicationData.Current.LocalFolder;
                _rootFolder = await localFolder.CreateFolderAsync(ROOT_FOLDER, CreationCollisionOption.OpenIfExists);
                _sessionsFolder = await _rootFolder.CreateFolderAsync(SESSIONS_FOLDER, CreationCollisionOption.OpenIfExists);
                _prekeysFolder = await _rootFolder.CreateFolderAsync(PREKEYS_FOLDER, CreationCollisionOption.OpenIfExists);
                _senderKeysFolder = await _rootFolder.CreateFolderAsync(SENDER_KEYS_FOLDER, CreationCollisionOption.OpenIfExists);
                _tcTokensFolder = await _rootFolder.CreateFolderAsync(TC_TOKENS_FOLDER, CreationCollisionOption.OpenIfExists);
                _appStateSyncKeysFolder = await _rootFolder.CreateFolderAsync(APP_STATE_SYNC_KEYS_FOLDER, CreationCollisionOption.OpenIfExists);
                _appStateSyncStateFolder = await _rootFolder.CreateFolderAsync(APP_STATE_SYNC_STATE_FOLDER, CreationCollisionOption.OpenIfExists);

                await Task.WhenAll(
                    LoadSessionsIntoCacheAsync(),
                    LoadPreKeysIntoCacheAsync(),
                    LoadAccountIntoCacheAsync());

                // Folder references and the critical caches are now safe for on-demand
                // reads even while the secondary warm-up continues.
                _initialized = true;
                _criticalInitialized = true;

                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "key-store-critical-loaded",
                    "milliseconds=" + started.ElapsedMilliseconds +
                    "; sessions=" + _sessionCache.Count +
                    "; prekeys=" + _preKeyCache.Count +
                    "; account=" + (_accountCache != null));
                Debug.WriteLine($"[KeyStore] Critical cache initialized in {started.ElapsedMilliseconds} ms. Sessions: {_sessionCache.Count}, PreKeys: {_preKeyCache.Count}, Account: {_accountCache != null}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to initialize critical cache: {ex.Message}");
                throw;
            }
            finally
            {
                _initializeLock.Release();
            }
        }

        /// <summary>
        /// Warms non-critical caches without delaying the socket handshake.
        /// Concurrent calls share the same task.
        /// </summary>
        public Task WarmSecondaryCachesAsync()
        {
            lock (_secondaryWarmupLock)
            {
                if (_fullInitialized)
                {
                    return Task.CompletedTask;
                }

                if (_secondaryWarmupTask == null || _secondaryWarmupTask.IsCanceled || _secondaryWarmupTask.IsFaulted)
                {
                    _secondaryWarmupTask = WarmSecondaryCachesCoreAsync();
                }

                return _secondaryWarmupTask;
            }
        }

        private async Task WarmSecondaryCachesCoreAsync()
        {
            await InitializeCriticalAsync();
            var started = Stopwatch.StartNew();

            await Task.WhenAll(
                LoadSenderKeysIntoCacheAsync(),
                LoadTcTokensIntoCacheAsync(),
                LoadAppStateSyncKeysIntoCacheAsync(),
                LoadAppStateCollectionStateIntoCacheAsync());

            _fullInitialized = true;
            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "key-store-secondary-warmed",
                "milliseconds=" + started.ElapsedMilliseconds +
                "; senderKeys=" + _senderKeyCache.Count +
                "; tcTokens=" + _tcTokenCache.Count +
                "; appStateKeys=" + _appStateSyncKeyCache.Count +
                "; appStateCollections=" + _appStateCollectionStateCache.Count);
            Debug.WriteLine($"[KeyStore] Secondary cache warmed in {started.ElapsedMilliseconds} ms. SenderKeys: {_senderKeyCache.Count}, TcTokens: {_tcTokenCache.Count}, AppStateKeys: {_appStateSyncKeyCache.Count}, AppStateCollections: {_appStateCollectionStateCache.Count}");
        }

        private async Task LoadJsonFilesBatchedAsync(
            StorageFolder folder,
            string category,
            Func<StorageFile, Task> loadFileAsync)
        {
            if (folder == null || loadFileAsync == null)
            {
                return;
            }

            var started = Stopwatch.StartNew();
            var files = (await folder.GetFilesAsync())
                .Where(file => string.Equals(file.FileType, ".json", StringComparison.OrdinalIgnoreCase))
                .ToList();

            for (int offset = 0; offset < files.Count; offset += KEY_LOAD_BATCH_SIZE)
            {
                var batch = new List<Task>(KEY_LOAD_BATCH_SIZE);
                int limit = Math.Min(files.Count, offset + KEY_LOAD_BATCH_SIZE);
                for (int index = offset; index < limit; index++)
                {
                    batch.Add(loadFileAsync(files[index]));
                }

                await Task.WhenAll(batch);
            }

            RuntimeDiagnosticsService.Instance.Write(
                "connection",
                "key-store-folder-loaded",
                "category=" + category +
                "; files=" + files.Count +
                "; milliseconds=" + started.ElapsedMilliseconds);
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

            lock (_preKeySnapshotTimerLock)
            {
                _preKeySnapshotTimer?.Dispose();
                _preKeySnapshotTimer = null;
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
            _criticalInitialized = false;
            _fullInitialized = false;
            lock (_secondaryWarmupLock)
            {
                _secondaryWarmupTask = null;
            }

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
                await LoadJsonFilesBatchedAsync(
                    _sessionsFolder,
                    "sessions",
                    async file =>
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
                    });
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
                        KeyPair = new KeyPair(
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
                await AppendPreKeyDeltaAsync(new PreKeyDeltaRecord
                {
                    Operation = "set",
                    Id = id,
                    Data = fileData
                });
                SchedulePreKeySnapshotCompaction();
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

                await AppendPreKeyDeltaAsync(new PreKeyDeltaRecord
                {
                    Operation = "remove",
                    Id = id
                });
                SchedulePreKeySnapshotCompaction();
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
                var started = Stopwatch.StartNew();
                bool snapshotLoaded = await TryLoadPreKeySnapshotAsync();
                int deltaCount = await ApplyPreKeyDeltaAsync();

                if (!snapshotLoaded)
                {
                    await LoadJsonFilesBatchedAsync(
                        _prekeysFolder,
                        "prekeys-legacy",
                        async file =>
                        {
                            try
                            {
                                var json = await FileIO.ReadTextAsync(file);
                                var data = JsonConvert.DeserializeObject<PreKeyFileData>(json);
                                ApplyPreKeyFileData(data);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[KeyStore] Failed to load pre-key file {file.Name}: {ex.Message}");
                            }
                        });

                    await CompactPreKeySnapshotAsync();
                }
                else if (deltaCount > 0)
                {
                    // Fold the tiny append-only delta into the one-file snapshot so
                    // the next process start needs only one read again.
                    await CompactPreKeySnapshotAsync();
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "prekey-cache-loaded",
                    "source=" + (snapshotLoaded ? "snapshot" : "legacy-files") +
                    "; count=" + _preKeyCache.Count +
                    "; delta=" + deltaCount +
                    "; milliseconds=" + started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to initialize pre-key cache: {ex.Message}");
            }
        }

        private void ApplyPreKeyFileData(PreKeyFileData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.PrivateKey) || string.IsNullOrWhiteSpace(data.PublicKey))
            {
                return;
            }

            try
            {
                _preKeyCache[data.Id] = new PreKeyData
                {
                    Id = data.Id,
                    KeyPair = new KeyPair(
                        Convert.FromBase64String(data.PrivateKey),
                        Convert.FromBase64String(data.PublicKey))
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Ignoring invalid pre-key {data.Id}: {ex.Message}");
            }
        }

        private async Task<bool> TryLoadPreKeySnapshotAsync()
        {
            try
            {
                var file = await _rootFolder.TryGetItemAsync(PREKEYS_CACHE_FILE) as StorageFile;
                if (file == null)
                {
                    return false;
                }

                var json = await FileIO.ReadTextAsync(file);
                var snapshot = JsonConvert.DeserializeObject<List<PreKeyFileData>>(json);
                if (snapshot == null)
                {
                    return false;
                }

                foreach (var item in snapshot)
                {
                    ApplyPreKeyFileData(item);
                }

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to load pre-key snapshot: {ex.Message}");
                return false;
            }
        }

        private async Task<int> ApplyPreKeyDeltaAsync()
        {
            try
            {
                var file = await _rootFolder.TryGetItemAsync(PREKEYS_DELTA_FILE) as StorageFile;
                if (file == null)
                {
                    return 0;
                }

                var text = await FileIO.ReadTextAsync(file);
                int applied = 0;
                var lines = (text ?? string.Empty).Split(
                    new[] { "\r\n", "\n" },
                    StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    try
                    {
                        var delta = JsonConvert.DeserializeObject<PreKeyDeltaRecord>(line);
                        if (delta == null)
                        {
                            continue;
                        }

                        if (string.Equals(delta.Operation, "remove", StringComparison.OrdinalIgnoreCase))
                        {
                            _preKeyCache.TryRemove(delta.Id, out _);
                            applied++;
                        }
                        else if (string.Equals(delta.Operation, "set", StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyPreKeyFileData(delta.Data);
                            applied++;
                        }
                    }
                    catch
                    {
                        // Ignore a partially-written final line; previous complete
                        // records remain valid and the individual files are retained.
                    }
                }

                return applied;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[KeyStore] Failed to load pre-key delta: {ex.Message}");
                return 0;
            }
        }

        private async Task AppendPreKeyDeltaAsync(PreKeyDeltaRecord delta)
        {
            if (delta == null || _rootFolder == null)
            {
                return;
            }

            await _preKeySnapshotLock.WaitAsync();
            try
            {
                var file = await _rootFolder.CreateFileAsync(
                    PREKEYS_DELTA_FILE,
                    CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(
                    file,
                    JsonConvert.SerializeObject(delta, Formatting.None) + "\n");
            }
            catch (Exception ex)
            {
                // The individual pre-key file remains the source of truth. Invalidate
                // the aggregate snapshot so the next cold start repairs it from the
                // legacy per-key files rather than trusting stale data.
                try
                {
                    var snapshotFile = await _rootFolder.TryGetItemAsync(PREKEYS_CACHE_FILE) as StorageFile;
                    if (snapshotFile != null)
                    {
                        await snapshotFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                    }
                }
                catch
                {
                }

                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "prekey-delta-append-failed",
                    ex,
                    "id=" + delta.Id + "; operation=" + delta.Operation);
            }
            finally
            {
                _preKeySnapshotLock.Release();
            }
        }

        private void SchedulePreKeySnapshotCompaction()
        {
            lock (_preKeySnapshotTimerLock)
            {
                _preKeySnapshotTimer?.Dispose();
                _preKeySnapshotTimer = new System.Threading.Timer(
                    _ =>
                    {
                        _ = CompactPreKeySnapshotSafelyAsync();
                    },
                    null,
                    2000,
                    Timeout.Infinite);
            }
        }

        private async Task CompactPreKeySnapshotSafelyAsync()
        {
            try
            {
                await CompactPreKeySnapshotAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "prekey-snapshot-write-failed",
                    ex);
            }
        }

        private async Task CompactPreKeySnapshotAsync()
        {
            if (_rootFolder == null)
            {
                return;
            }

            lock (_preKeySnapshotTimerLock)
            {
                _preKeySnapshotTimer?.Dispose();
                _preKeySnapshotTimer = null;
            }

            await _preKeySnapshotLock.WaitAsync();
            try
            {
                var snapshot = _preKeyCache
                    .OrderBy(pair => pair.Key)
                    .Select(pair => new PreKeyFileData
                    {
                        Id = pair.Key,
                        PrivateKey = Convert.ToBase64String(pair.Value.KeyPair.Private),
                        PublicKey = Convert.ToBase64String(pair.Value.KeyPair.Public)
                    })
                    .ToList();

                var file = await _rootFolder.CreateFileAsync(
                    PREKEYS_CACHE_FILE,
                    CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(
                    file,
                    JsonConvert.SerializeObject(snapshot, Formatting.None));

                var deltaFile = await _rootFolder.TryGetItemAsync(PREKEYS_DELTA_FILE) as StorageFile;
                if (deltaFile != null)
                {
                    await deltaFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "prekey-snapshot-written",
                    "count=" + snapshot.Count);
            }
            finally
            {
                _preKeySnapshotLock.Release();
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
                await LoadJsonFilesBatchedAsync(
                    _senderKeysFolder,
                    "sender-keys",
                    async file =>
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
                    });
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

            // Compatibility fallback for users upgrading from the multi-file format.
            // Only the requested token is read; the 600+ file enumeration is avoided.
            var fileLock = GetFileLock("tctoken-" + normalizedJid);
            await fileLock.WaitAsync();
            try
            {
                var fileName = SanitizeFileName(normalizedJid) + ".json";
                var file = await _tcTokensFolder.TryGetItemAsync(fileName) as StorageFile;
                if (file == null) return null;

                var json = await FileIO.ReadTextAsync(file);
                var data = JsonConvert.DeserializeObject<TcTokenFileData>(json);
                var token = ConvertTcTokenFileData(data);
                if (token == null) return null;

                _tcTokenCache[normalizedJid] = CloneTcTokenData(token);
                return token;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeyStore] Failed to read tctoken for " + normalizedJid + ": " + ex.Message);
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

            QueueTcTokenDelta(new TcTokenDeltaRecord
            {
                Operation = "set",
                Jid = normalizedJid,
                Data = CreateTcTokenFileData(normalizedJid, clone)
            });
            ScheduleTcTokenSnapshotCompaction();
            await Task.CompletedTask;
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
            QueueTcTokenDelta(new TcTokenDeltaRecord
            {
                Operation = "remove",
                Jid = normalizedJid
            });
            ScheduleTcTokenSnapshotCompaction();
            await Task.CompletedTask;
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
                var started = Stopwatch.StartNew();
                bool snapshotLoaded = await TryLoadTcTokenSnapshotAsync();
                int deltaCount = await ApplyTcTokenDeltaAsync();

                if (!snapshotLoaded)
                {
                    await LoadJsonFilesBatchedAsync(
                        _tcTokensFolder,
                        "tc-tokens-legacy",
                        async file =>
                        {
                            try
                            {
                                var json = await FileIO.ReadTextAsync(file);
                                ApplyTcTokenFileData(JsonConvert.DeserializeObject<TcTokenFileData>(json));
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("[KeyStore] Failed to load tctoken file " + file.Name + ": " + ex.Message);
                            }
                        });

                    await CompactTcTokenSnapshotAsync();
                }
                else if (deltaCount > 0)
                {
                    await CompactTcTokenSnapshotAsync();
                }

                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "tc-token-cache-loaded",
                    "source=" + (snapshotLoaded ? "snapshot" : "legacy-files") +
                    "; count=" + _tcTokenCache.Count +
                    "; delta=" + deltaCount +
                    "; milliseconds=" + started.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeyStore] Failed to initialize tctoken cache: " + ex.Message);
            }
        }

        private static TcTokenFileData CreateTcTokenFileData(string jid, TcTokenData data)
        {
            return new TcTokenFileData
            {
                Jid = jid,
                Token = data == null || data.Token == null || data.Token.Length == 0 ? null : Convert.ToBase64String(data.Token),
                Timestamp = data?.Timestamp,
                SenderTimestamp = data?.SenderTimestamp,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }

        private static TcTokenData ConvertTcTokenFileData(TcTokenFileData data)
        {
            if (data == null || string.IsNullOrWhiteSpace(data.Jid))
            {
                return null;
            }

            try
            {
                return new TcTokenData
                {
                    Token = string.IsNullOrEmpty(data.Token) ? null : Convert.FromBase64String(data.Token),
                    Timestamp = data.Timestamp,
                    SenderTimestamp = data.SenderTimestamp
                };
            }
            catch
            {
                return null;
            }
        }

        private void ApplyTcTokenFileData(TcTokenFileData data)
        {
            var token = ConvertTcTokenFileData(data);
            if (token == null) return;

            string normalizedJid = WA.GetBaseJid(WA.NormalizeDeviceJid(data.Jid));
            if (!string.IsNullOrWhiteSpace(normalizedJid))
            {
                _tcTokenCache[normalizedJid] = token;
            }
        }

        private async Task<bool> TryLoadTcTokenSnapshotAsync()
        {
            try
            {
                var file = await _rootFolder.TryGetItemAsync(TC_TOKENS_CACHE_FILE) as StorageFile;
                if (file == null) return false;

                var json = await FileIO.ReadTextAsync(file);
                var snapshot = JsonConvert.DeserializeObject<List<TcTokenFileData>>(json);
                if (snapshot == null) return false;

                foreach (var item in snapshot)
                {
                    ApplyTcTokenFileData(item);
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeyStore] Failed to load tctoken snapshot: " + ex.Message);
                return false;
            }
        }

        private async Task<int> ApplyTcTokenDeltaAsync()
        {
            try
            {
                var file = await _rootFolder.TryGetItemAsync(TC_TOKENS_DELTA_FILE) as StorageFile;
                if (file == null) return 0;

                var text = await FileIO.ReadTextAsync(file);
                int applied = 0;
                var lines = (text ?? string.Empty).Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in lines)
                {
                    try
                    {
                        var delta = JsonConvert.DeserializeObject<TcTokenDeltaRecord>(line);
                        if (delta == null || string.IsNullOrWhiteSpace(delta.Jid)) continue;

                        string normalizedJid = WA.GetBaseJid(WA.NormalizeDeviceJid(delta.Jid));
                        if (string.Equals(delta.Operation, "remove", StringComparison.OrdinalIgnoreCase))
                        {
                            _tcTokenCache.TryRemove(normalizedJid, out _);
                            applied++;
                        }
                        else if (string.Equals(delta.Operation, "set", StringComparison.OrdinalIgnoreCase))
                        {
                            ApplyTcTokenFileData(delta.Data);
                            applied++;
                        }
                    }
                    catch
                    {
                        // Ignore an incomplete final line after an abrupt process stop.
                    }
                }
                return applied;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[KeyStore] Failed to load tctoken delta: " + ex.Message);
                return 0;
            }
        }

        private void QueueTcTokenDelta(TcTokenDeltaRecord delta)
        {
            if (delta == null) return;
            _tcTokenDeltaQueue.Enqueue(delta);

            lock (_tcTokenDeltaTimerLock)
            {
                if (_tcTokenDeltaTimer == null)
                {
                    _tcTokenDeltaTimer = new System.Threading.Timer(
                        _ => { _ = FlushQueuedTcTokenDeltasSafelyAsync(); },
                        null,
                        250,
                        Timeout.Infinite);
                }
            }
        }

        private async Task FlushQueuedTcTokenDeltasSafelyAsync()
        {
            try
            {
                await FlushQueuedTcTokenDeltasAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException(
                    "connection",
                    "tc-token-delta-flush-failed",
                    ex);
            }
        }

        private async Task FlushQueuedTcTokenDeltasAsync()
        {
            lock (_tcTokenDeltaTimerLock)
            {
                _tcTokenDeltaTimer?.Dispose();
                _tcTokenDeltaTimer = null;
            }

            var pending = new List<TcTokenDeltaRecord>();
            while (_tcTokenDeltaQueue.TryDequeue(out var item))
            {
                if (item != null) pending.Add(item);
            }
            if (pending.Count == 0) return;

            await _tcTokenSnapshotLock.WaitAsync();
            try
            {
                var file = await _rootFolder.CreateFileAsync(TC_TOKENS_DELTA_FILE, CreationCollisionOption.OpenIfExists);
                var text = string.Join("\n", pending.Select(item => JsonConvert.SerializeObject(item, Formatting.None))) + "\n";
                await FileIO.AppendTextAsync(file, text);
                RuntimeDiagnosticsService.Instance.Write(
                    "connection",
                    "tc-token-delta-batch-written",
                    "count=" + pending.Count);
            }
            catch
            {
                foreach (var item in pending)
                {
                    _tcTokenDeltaQueue.Enqueue(item);
                }
                throw;
            }
            finally
            {
                _tcTokenSnapshotLock.Release();
            }
        }

        private async Task AppendTcTokenDeltaAsync(TcTokenDeltaRecord delta)
        {
            if (delta == null || _rootFolder == null) return;

            await _tcTokenSnapshotLock.WaitAsync();
            try
            {
                var file = await _rootFolder.CreateFileAsync(TC_TOKENS_DELTA_FILE, CreationCollisionOption.OpenIfExists);
                await FileIO.AppendTextAsync(file, JsonConvert.SerializeObject(delta, Formatting.None) + "\n");
            }
            finally
            {
                _tcTokenSnapshotLock.Release();
            }
        }

        private void ScheduleTcTokenSnapshotCompaction()
        {
            lock (_tcTokenSnapshotTimerLock)
            {
                _tcTokenSnapshotTimer?.Dispose();
                _tcTokenSnapshotTimer = new System.Threading.Timer(
                    _ => { _ = CompactTcTokenSnapshotSafelyAsync(); },
                    null,
                    2500,
                    Timeout.Infinite);
            }
        }

        private async Task CompactTcTokenSnapshotSafelyAsync()
        {
            try
            {
                await CompactTcTokenSnapshotAsync();
            }
            catch (Exception ex)
            {
                RuntimeDiagnosticsService.Instance.RecordException("connection", "tc-token-snapshot-write-failed", ex);
            }
        }

        private async Task CompactTcTokenSnapshotAsync()
        {
            if (_rootFolder == null) return;

            lock (_tcTokenSnapshotTimerLock)
            {
                _tcTokenSnapshotTimer?.Dispose();
                _tcTokenSnapshotTimer = null;
            }

            await FlushQueuedTcTokenDeltasAsync();
            await _tcTokenSnapshotLock.WaitAsync();
            try
            {
                var snapshot = _tcTokenCache
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => CreateTcTokenFileData(pair.Key, pair.Value))
                    .ToList();

                var file = await _rootFolder.CreateFileAsync(TC_TOKENS_CACHE_FILE, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, JsonConvert.SerializeObject(snapshot, Formatting.None));

                var deltaFile = await _rootFolder.TryGetItemAsync(TC_TOKENS_DELTA_FILE) as StorageFile;
                if (deltaFile != null)
                {
                    await deltaFile.DeleteAsync(StorageDeleteOption.PermanentDelete);
                }

                RuntimeDiagnosticsService.Instance.Write("connection", "tc-token-snapshot-written", "count=" + snapshot.Count);
            }
            finally
            {
                _tcTokenSnapshotLock.Release();
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
                await LoadJsonFilesBatchedAsync(
                    _appStateSyncKeysFolder,
                    "app-state-keys",
                    async file =>
                    {
                        try
                        {
                            var json = await FileIO.ReadTextAsync(file);
                            var data = JsonConvert.DeserializeObject<AppStateSyncKeyFileData>(json);
                            if (string.IsNullOrWhiteSpace(data?.KeyId) || string.IsNullOrWhiteSpace(data.ProtoData))
                            {
                                return;
                            }

                            _appStateSyncKeyCache[data.KeyId] =
                                Message.Types.AppStateSyncKeyData.Parser.ParseFrom(Convert.FromBase64String(data.ProtoData));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[KeyStore] Failed to load app-state key file {file.Name}: {ex.Message}");
                        }
                    });
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
                await LoadJsonFilesBatchedAsync(
                    _appStateSyncStateFolder,
                    "app-state-collections",
                    async file =>
                    {
                        try
                        {
                            var json = await FileIO.ReadTextAsync(file);
                            var data = JsonConvert.DeserializeObject<AppStateCollectionStateFileData>(json);
                            if (string.IsNullOrWhiteSpace(data?.Name))
                            {
                                return;
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
                    });
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

        private class PreKeyDeltaRecord
        {
            public string Operation { get; set; }
            public int Id { get; set; }
            public PreKeyFileData Data { get; set; }
        }

        private class SenderKeyFileData
        {
            public string GroupJid { get; set; }
            public string SenderJid { get; set; }
            public string SenderKeyData { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        private class TcTokenDeltaRecord
        {
            public string Operation { get; set; }
            public string Jid { get; set; }
            public TcTokenFileData Data { get; set; }
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
