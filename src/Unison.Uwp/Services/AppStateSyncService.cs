using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Newtonsoft.Json.Linq;
using Proto;
using Unison.Uwp.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;

using Unison.Baileys.Client;
using Unison.Uwp.Services.WhatsApp;

namespace Unison.Uwp.Services
{
    internal sealed class AppStateSyncService
    {
        private static readonly string[] DefaultCollections = { "critical_block", "critical_unblock_low", "regular_high", "regular_low", "regular" };
        private const string MutationKeysInfo = "WhatsApp Mutation Keys";
        private const string PatchIntegrityInfo = "WhatsApp Patch Integrity";
        private const int MaxSyncAttempts = 2;

        private readonly SocketClient _socket;
        private readonly AuthState _authState;
        private readonly AuthStore _authStore;
        private readonly IKeyStore _keyStore;
        private readonly WhatsAppService _owner;
        private readonly SemaphoreSlim _syncLock = new SemaphoreSlim(1, 1);
        private bool _initialSyncComplete;
        private bool _pendingInitialSync;
        private DateTime _lastKeyBootstrapRequestUtc = DateTime.MinValue;
        private static readonly TimeSpan KeyBootstrapCooldown = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan FatalRecoveryCooldown = TimeSpan.FromSeconds(60);
        private int _bootstrapRetryScheduled;
        private int _bootstrapResponseWatchScheduled;
        private readonly HashSet<string> _pendingBootstrapRequestIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly object _bootstrapLock = new object();
        private string _lastBootstrapSignature;
        private string _suppressedBootstrapSignature;
        private string _lastBootstrapReason;
        private readonly object _fatalRecoveryLock = new object();
        private readonly Dictionary<string, string> _pendingFatalRecoveryByStanzaId = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> _fatalRecoveryCollectionsInFlight = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _lastFatalRecoveryRequestUtcByCollection = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        private sealed class MutationKeys
        {
            public byte[] IndexKey { get; set; }
            public byte[] ValueEncryptionKey { get; set; }
            public byte[] ValueMacKey { get; set; }
            public byte[] SnapshotMacKey { get; set; }
            public byte[] PatchMacKey { get; set; }
        }

        private sealed class CollectionChunk
        {
            public string Name { get; set; }
            public bool HasMorePatches { get; set; }
            public SyncdSnapshot Snapshot { get; set; }
            public List<SyncdPatch> Patches { get; } = new List<SyncdPatch>();
        }

        private sealed class DecodedMutation
        {
            public SyncActionData SyncAction { get; set; }
            public JArray Index { get; set; }
            public SyncdMutation.Types.SyncdOperation Operation { get; set; }
        }

        private sealed class DecodedMutationsResult
        {
            public byte[] Hash { get; set; }
            public Dictionary<string, byte[]> IndexValueMap { get; set; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            public Dictionary<string, DecodedMutation> MutationMap { get; set; } = new Dictionary<string, DecodedMutation>(StringComparer.Ordinal);
        }

        private sealed class CollectionDecodeResult
        {
            public AppStateCollectionState State { get; set; }
            public Dictionary<string, DecodedMutation> MutationMap { get; set; } = new Dictionary<string, DecodedMutation>(StringComparer.Ordinal);
        }

        private sealed class MutationEnvelope
        {
            public SyncdMutation.Types.SyncdOperation Operation { get; set; }
            public SyncdRecord Record { get; set; }
        }

        private sealed class LtHashAccumulator
        {
            private readonly Dictionary<string, byte[]> _indexValueMap;
            private readonly List<byte[]> _addList = new List<byte[]>();
            private readonly List<byte[]> _subtractList = new List<byte[]>();
            private readonly byte[] _hash;
            private readonly string _collectionName;
            private readonly bool _allowMissingRemove;

            public LtHashAccumulator(string collectionName, AppStateCollectionState initial)
            {
                _collectionName = collectionName ?? string.Empty;
                _allowMissingRemove = string.Equals(_collectionName, "regular_low", StringComparison.Ordinal);
                _hash = initial?.Hash != null && initial.Hash.Length == 128 ? (byte[])initial.Hash.Clone() : new byte[128];
                _indexValueMap = initial?.IndexValueMap != null
                    ? initial.IndexValueMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value != null ? (byte[])kvp.Value.Clone() : null, StringComparer.Ordinal)
                    : new Dictionary<string, byte[]>(StringComparer.Ordinal);
            }

            public void Mix(byte[] indexMac, byte[] valueMac, SyncdMutation.Types.SyncdOperation operation)
            {
                string indexKey = Convert.ToBase64String(indexMac ?? new byte[0]);
                _indexValueMap.TryGetValue(indexKey, out var previousValueMac);
                if (operation == SyncdMutation.Types.SyncdOperation.Remove)
                {
                    if (previousValueMac == null)
                    {
                        if (_allowMissingRemove)
                        {
                            WhatsAppService.Log($"[AppStateSync] {_collectionName}: skipping REMOVE with no previous value (index={indexKey}, known={_indexValueMap.Count})");
                            return;
                        }
                        throw new InvalidOperationException($"Tried REMOVE app-state mutation without a previous value (index={indexKey}, known={_indexValueMap.Count})");
                    }

                    _indexValueMap.Remove(indexKey);
                }
                else
                {
                    _addList.Add((byte[])valueMac.Clone());
                    _indexValueMap[indexKey] = (byte[])valueMac.Clone();
                }

                if (previousValueMac != null)
                {
                    _subtractList.Add((byte[])previousValueMac.Clone());
                }
            }

            public AppStateCollectionState Finish(string name, long version)
            {
                byte[] current = (byte[])_hash.Clone();
                foreach (var subtract in _subtractList)
                {
                    current = ApplyLtHash(current, subtract, true);
                }

                foreach (var add in _addList)
                {
                    current = ApplyLtHash(current, add, false);
                }

                return new AppStateCollectionState
                {
                    Name = name,
                    Version = version,
                    Hash = current,
                    IndexValueMap = _indexValueMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value != null ? (byte[])kvp.Value.Clone() : null, StringComparer.Ordinal)
                };
            }

            private static byte[] ApplyLtHash(byte[] current, byte[] valueMac, bool subtractMode)
            {
                byte[] patch = CryptoUtils.Hkdf(valueMac ?? new byte[0], 128, null, PatchIntegrityInfo);
                byte[] output = new byte[128];
                for (int i = 0; i < output.Length; i += 2)
                {
                    ushort currentWord = BitConverter.ToUInt16(current, i);
                    ushort patchWord = BitConverter.ToUInt16(patch, i);
                    ushort resultWord = unchecked((ushort)(subtractMode ? currentWord - patchWord : currentWord + patchWord));
                    byte[] wordBytes = BitConverter.GetBytes(resultWord);
                    output[i] = wordBytes[0];
                    output[i + 1] = wordBytes[1];
                }

                return output;
            }
        }

        public AppStateSyncService(SocketClient socket, AuthState authState, AuthStore authStore, IKeyStore keyStore, WhatsAppService owner)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _authState = authState ?? throw new ArgumentNullException(nameof(authState));
            _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
            _keyStore = keyStore ?? throw new ArgumentNullException(nameof(keyStore));
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        }

        public async Task HandleProtocolMessageAsync(Message.Types.ProtocolMessage protocolMessage)
        {
            var share = protocolMessage?.AppStateSyncKeyShare;
            if (share == null)
            {
                return;
            }

            if (share.Keys == null || share.Keys.Count == 0)
            {
                WhatsAppService.Log("[AppStateSync] Received AppStateSyncKeyShare with 0 keys");
                return;
            }

            int stored = 0;
            foreach (var key in share.Keys)
            {
                if (key?.KeyId?.HasKeyId != true || key.KeyData == null)
                {
                    continue;
                }

                string keyId = Convert.ToBase64String(key.KeyId.KeyId.ToByteArray());
                await _keyStore.SetAppStateSyncKeyAsync(keyId, key.KeyData);
                _authState.MyAppStateKeyId = keyId;
                stored++;
            }

            if (stored > 0)
            {
                lock (_bootstrapLock)
                {
                    _pendingBootstrapRequestIds.Clear();
                    _lastBootstrapSignature = null;
                    _suppressedBootstrapSignature = null;
                }
                await _authStore.SaveAsync(_authState);
                WhatsAppService.Log($"[AppStateSync] Stored {stored} app-state sync key(s); currentKey={_authState.MyAppStateKeyId}");
                if (_pendingInitialSync)
                {
                    _pendingInitialSync = false;
                    await EnsureCollectionsResyncedAsync(DefaultCollections, !_initialSyncComplete, "pending-key-share");
                }
            }
        }

        public async Task HandleFatalExceptionNotificationAsync(Message.Types.AppStateFatalExceptionNotification notification)
        {
            if (notification == null)
            {
                return;
            }

            var collections = notification.CollectionNames?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList()
                ?? new List<string>();
            if (collections.Count == 0)
            {
                collections = DefaultCollections.ToList();
            }

            long timestamp = notification.HasTimestamp
                ? notification.Timestamp
                : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

            WhatsAppService.Log($"[AppStateSync] App-state fatal exception notification: collections={string.Join(",", collections)}, timestamp={timestamp}");
            foreach (var collection in collections)
            {
                await RequestFatalRecoveryAsync(collection, timestamp, "fatal-exception-notification");
            }
        }

        public async Task HandlePeerDataOperationResponseAsync(Message.Types.PeerDataOperationRequestResponseMessage response)
        {
            if (response == null ||
                response.PeerDataOperationRequestType != Message.Types.PeerDataOperationRequestType.CompanionSyncdSnapshotFatalRecovery)
            {
                return;
            }

            string collectionName = null;
            lock (_fatalRecoveryLock)
            {
                if (!string.IsNullOrWhiteSpace(response.StanzaId))
                {
                    _pendingFatalRecoveryByStanzaId.TryGetValue(response.StanzaId, out collectionName);
                    if (!string.IsNullOrWhiteSpace(collectionName))
                    {
                        _pendingFatalRecoveryByStanzaId.Remove(response.StanzaId);
                        _fatalRecoveryCollectionsInFlight.Remove(collectionName);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(collectionName))
            {
                WhatsAppService.Log($"[AppStateSync] Fatal recovery response has no matching request: stanzaId={response.StanzaId}, resultCount={response.PeerDataOperationResult?.Count ?? 0}");
                return;
            }

            bool applied = false;
            foreach (var result in response.PeerDataOperationResult ?? Enumerable.Empty<Message.Types.PeerDataOperationRequestResponseMessage.Types.PeerDataOperationResult>())
            {
                var recovery = result.SyncdSnapshotFatalRecoveryResponse;
                if (recovery == null)
                {
                    continue;
                }

                try
                {
                    var bytes = recovery.CollectionSnapshot?.ToByteArray();
                    if (bytes == null || bytes.Length == 0)
                    {
                        WhatsAppService.Log($"[AppStateSync] Fatal recovery response for {collectionName} has empty snapshot: stanzaId={response.StanzaId}");
                        continue;
                    }

                    if (recovery.IsCompressed)
                    {
                        var decompressed = DecompressFatalRecoverySnapshot(bytes);
                        WhatsAppService.Log($"[AppStateSync] Fatal recovery snapshot decompressed for {collectionName}: {bytes.Length}->{decompressed.Length} bytes");
                        bytes = decompressed;
                    }

                    var snapshot = SyncdSnapshot.Parser.ParseFrom(bytes);
                    await ApplyRecoveredSnapshotAsync(collectionName, snapshot, $"fatal-recovery:{response.StanzaId}");
                    applied = true;
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[AppStateSync] Fatal recovery response failed for {collectionName}: {ex.Message}");
                }
            }

            if (applied)
            {
                await EnsureCollectionsResyncedAsync(new[] { collectionName }, false, $"fatal-recovery-response:{collectionName}");
            }
            else
            {
                WhatsAppService.Log($"[AppStateSync] Fatal recovery response for {collectionName} did not contain an applicable snapshot: stanzaId={response.StanzaId}");
            }
        }

        public async Task HandleReconnectCompletedAsync(int offlineCount)
        {
            ResetBootstrapSessionState();
            await EnsureCollectionsResyncedAsync(DefaultCollections, !_initialSyncComplete, $"reconnect:{offlineCount}");
        }

        public async Task EnsureBootstrapAsync(string reason)
        {
            if (reason != null && reason.IndexOf("session-initialized", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                ResetBootstrapSessionState();
            }
            await EnsureCollectionsResyncedAsync(DefaultCollections, !_initialSyncComplete, reason);
        }

        public async Task HandleDirtyNotificationAsync(string type, string timestamp)
        {
            if (!string.Equals(type, "account_sync", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (long.TryParse(timestamp, out var parsedTimestamp))
            {
                _authState.LastAccountSyncTimestamp = parsedTimestamp;
                await _authStore.SaveAsync(_authState);
            }

            await EnsureCollectionsResyncedAsync(DefaultCollections, false, $"dirty:{timestamp}");
        }

        public async Task ForceCollectionSnapshotAsync(string collectionName, string reason)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return;
            }

            // Reset only the requested collection. QueryCollectionsAsync sees version
            // zero and asks WhatsApp for a complete snapshot, allowing upgrades to
            // recover older actions (such as existing pinned chats) that were already
            // consumed by a previous client version.
            await _keyStore.SetAppStateCollectionStateAsync(
                collectionName,
                NewState(collectionName));
            await EnsureCollectionsResyncedAsync(
                new[] { collectionName },
                true,
                $"forced-snapshot:{reason}:{collectionName}");
        }

        public async Task HandleServerSyncCollectionAsync(string collectionName)
        {
            var mappedCollections = ExpandCollections(collectionName);
            if (mappedCollections.Count == 0)
            {
                await EnsureCollectionsResyncedAsync(DefaultCollections, false, "server_sync:*");
                return;
            }

            await EnsureCollectionsResyncedAsync(mappedCollections, false, $"server_sync:{collectionName}");
        }

        public void HandleAckNode(BinaryNode node)
        {
            var id = node?.Attrs?.GetDictionaryValueOrDefault("id");
            if (string.IsNullOrWhiteSpace(id))
            {
                return;
            }

            string error = node.Attrs.GetDictionaryValueOrDefault("error");

            bool matched;
            lock (_bootstrapLock)
            {
                matched = _pendingBootstrapRequestIds.Remove(id);
            }

            if (!matched)
            {
                string fatalRecoveryCollection = null;
                lock (_fatalRecoveryLock)
                {
                    if (_pendingFatalRecoveryByStanzaId.TryGetValue(id, out fatalRecoveryCollection))
                    {
                        if (!string.IsNullOrWhiteSpace(error))
                        {
                            _pendingFatalRecoveryByStanzaId.Remove(id);
                            _fatalRecoveryCollectionsInFlight.Remove(fatalRecoveryCollection);
                            _lastFatalRecoveryRequestUtcByCollection.Remove(fatalRecoveryCollection);
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(fatalRecoveryCollection))
                {
                    if (!string.IsNullOrWhiteSpace(error))
                    {
                        WhatsAppService.Log($"[AppStateSync] Fatal snapshot recovery ack rejected: collection={fatalRecoveryCollection}, stanzaId={id}, error={error}");
                    }
                    else
                    {
                        WhatsAppService.Log($"[AppStateSync] Fatal snapshot recovery ack accepted: collection={fatalRecoveryCollection}, stanzaId={id}");
                    }
                    return;
                }

                return;
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                WhatsAppService.Log($"[AppStateSync] App-state key bootstrap ack rejected: stanzaId={id}, error={error}");
                lock (_bootstrapLock)
                {
                    _suppressedBootstrapSignature = null;
                    _lastBootstrapSignature = null;
                }

                ScheduleBootstrapRetry();
                return;
            }

            WhatsAppService.Log($"[AppStateSync] App-state key bootstrap ack accepted: stanzaId={id}");
            ScheduleBootstrapResponseTimeoutWatch(id);
        }

        private async Task EnsureCollectionsResyncedAsync(IEnumerable<string> collections, bool isInitialSync, string reason)
        {
            var targetCollections = collections?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
            if (targetCollections.Count == 0)
            {
                return;
            }

            await _syncLock.WaitAsync();
            try
            {
                var allKeys = await _keyStore.GetAllAppStateSyncKeysAsync();
                if (allKeys == null || allKeys.Count == 0)
                {
                    _pendingInitialSync = true;
                    await EnsureKeyBootstrapRequestedAsync(reason, targetCollections);
                    WhatsAppService.Log($"[AppStateSync] Resync deferred until app-state keys arrive (reason={reason}, collections={string.Join(",", targetCollections)})");
                    return;
                }

                WhatsAppService.Log($"[AppStateSync] Resync start reason={reason}, initial={isInitialSync}, collections={string.Join(",", targetCollections)}");
                var pending = new HashSet<string>(targetCollections, StringComparer.Ordinal);
                var attempts = new Dictionary<string, int>(StringComparer.Ordinal);
                var forceSnapshotCollections = new HashSet<string>(StringComparer.Ordinal);
                var blockedCollections = new HashSet<string>(StringComparer.Ordinal);
                var appliedMutations = new Dictionary<string, Dictionary<string, DecodedMutation>>(StringComparer.Ordinal);

                while (pending.Count > 0)
                {
                    var chunks = await QueryCollectionsAsync(pending, forceSnapshotCollections);
                    if (chunks.Count == 0)
                    {
                        WhatsAppService.Log($"[AppStateSync] No collections returned for reason={reason}; stopping current pass");
                        break;
                    }
                    foreach (var name in chunks.Keys.ToList())
                    {
                        var chunk = chunks[name];
                        try
                        {
                            var state = await _keyStore.GetAppStateCollectionStateAsync(name) ?? NewState(name);
                            long initialVersion = state.Version;
                            if (chunk.Snapshot != null || chunk.Patches.Count > 0)
                            {
                                long snapshotVersion = chunk.Snapshot?.Version != null ? unchecked((long)chunk.Snapshot.Version.Version) : -1;
                                string patchVersions = string.Join(",", chunk.Patches.Select(p => p?.Version != null ? unchecked((long)p.Version.Version).ToString() : "null"));
                                WhatsAppService.Log($"[AppStateSync] {name}: storedVersion={initialVersion}, snapshotVersion={(snapshotVersion >= 0 ? snapshotVersion.ToString() : "none")}, patchVersions=[{patchVersions}]");
                            }
                            var mutationsForCollection = new Dictionary<string, DecodedMutation>(StringComparer.Ordinal);

                            if (chunk.Snapshot != null)
                            {
                                var snapshotResult = await DecodeSnapshotAsync(name, chunk.Snapshot, initialVersion);
                                state = snapshotResult.State;
                                MergeMutationMaps(mutationsForCollection, snapshotResult.MutationMap);
                                await _keyStore.SetAppStateCollectionStateAsync(name, state);
                                WhatsAppService.Log($"[AppStateSync] {name}: snapshot restored to v{state.Version} (mutations={snapshotResult.MutationMap.Count})");
                            }

                            if (chunk.Patches.Count > 0)
                            {
                                var patchResult = await DecodePatchesAsync(name, chunk.Patches, state, initialVersion);
                                state = patchResult.State;
                                MergeMutationMaps(mutationsForCollection, patchResult.MutationMap);
                                await _keyStore.SetAppStateCollectionStateAsync(name, state);
                                WhatsAppService.Log($"[AppStateSync] {name}: patch sync advanced to v{state.Version} (patches={chunk.Patches.Count}, mutations={patchResult.MutationMap.Count}, hasMore={chunk.HasMorePatches})");
                            }
                            else
                            {
                                WhatsAppService.Log($"[AppStateSync] {name}: no patch payloads returned (snapshot={chunk.Snapshot != null}, hasMore={chunk.HasMorePatches})");
                            }

                            appliedMutations[name] = mutationsForCollection;
                            if (!chunk.HasMorePatches)
                            {
                                pending.Remove(name);
                            }
                        }
                        catch (Exception ex)
                        {
                            int attempt = attempts.TryGetValue(name, out var existing) ? existing + 1 : 1;
                            attempts[name] = attempt;

                            if (TryExtractMissingAppStateSyncKeyId(ex, out var missingKeyId))
                            {
                                _pendingInitialSync = true;
                                if (attempt < MaxSyncAttempts)
                                {
                                    forceSnapshotCollections.Add(name);
                                    WhatsAppService.Log($"[AppStateSync] {name}: missing key {missingKeyId} at attempt {attempt}; forcing snapshot retry");
                                    continue;
                                }

                                blockedCollections.Add(name);
                                pending.Remove(name);
                                await RequestAppStateSyncKeysByIdAsync(new[] { missingKeyId }, $"missing-key:{missingKeyId}:{reason}");
                                WhatsAppService.Log($"[AppStateSync] {name}: parked waiting for app-state key share (keyId={missingKeyId}, attempts={attempt}, reason={reason})");
                                continue;
                            }

                            WhatsAppService.Log($"[AppStateSync] {name}: sync failed on attempt {attempt}; {ex.Message}");
                            if (attempt >= MaxSyncAttempts)
                            {
                                pending.Remove(name);
                                WhatsAppService.Log($"[AppStateSync] {name}: giving up after retry-from-scratch");
                            }
                            else
                            {
                                forceSnapshotCollections.Add(name);
                                WhatsAppService.Log($"[AppStateSync] {name}: forcing snapshot retry after recoverable sync failure");
                            }
                        }
                    }
                }

                foreach (var entry in appliedMutations)
                {
                    await ApplyMutationsAsync(entry.Key, entry.Value, isInitialSync);
                }

                if (blockedCollections.Count == 0 && targetCollections.Any(c => DefaultCollections.Contains(c, StringComparer.Ordinal)))
                {
                    _pendingInitialSync = false;
                    _initialSyncComplete = true;
                }

                if (blockedCollections.Count > 0)
                {
                    WhatsAppService.Log($"[AppStateSync] Resync incomplete reason={reason}, blocked={string.Join(",", blockedCollections)}");
                }
                else
                {
                    WhatsAppService.Log($"[AppStateSync] Resync complete reason={reason}, collections={string.Join(",", targetCollections)}");
                }
            }
            finally
            {
                _syncLock.Release();
            }
        }

        private async Task EnsureKeyBootstrapRequestedAsync(string reason, IEnumerable<string> collections)
        {
            var now = DateTime.UtcNow;
            if (now - _lastKeyBootstrapRequestUtc < KeyBootstrapCooldown)
            {
                return;
            }

            _lastKeyBootstrapRequestUtc = now;
            try
            {
                var allKeys = await _keyStore.GetAllAppStateSyncKeysAsync();
                var existingKeyIdSet = new HashSet<string>(
                    (allKeys ?? new Dictionary<string, Message.Types.AppStateSyncKeyData>()).Keys,
                    StringComparer.Ordinal);

                var missingKeyIds = new List<byte[]>();
                var missingKeyIdSet = new HashSet<string>(StringComparer.Ordinal);
                foreach (var probed in await ProbeAppStateKeyIdsAsync(collections))
                {
                    string key = Convert.ToBase64String(probed);
                    if (existingKeyIdSet.Contains(key))
                    {
                        continue;
                    }

                    if (missingKeyIdSet.Add(key))
                    {
                        missingKeyIds.Add(probed);
                    }
                }

                if (missingKeyIds.Count == 0)
                {
                    WhatsAppService.Log($"[AppStateSync] No missing app-state key ids to request (reason={reason})");
                    return;
                }

                string signature = string.Join(",", missingKeyIdSet.OrderBy(k => k, StringComparer.Ordinal));
                lock (_bootstrapLock)
                {
                    if (!string.IsNullOrEmpty(signature) &&
                        string.Equals(_suppressedBootstrapSignature, signature, StringComparison.Ordinal))
                    {
                        WhatsAppService.Log($"[AppStateSync] Suppressing duplicate app-state key bootstrap for unchanged key id set: {signature}");
                        return;
                    }
                }

                WhatsAppService.Log($"[AppStateSync] Requesting app-state key bootstrap (reason={reason}, missingIds={missingKeyIds.Count}): {signature}");
                string stanzaId = await _socket.RequestAppStateSyncKeyShareAsync(missingKeyIds);
                lock (_bootstrapLock)
                {
                    _pendingBootstrapRequestIds.Add(stanzaId);
                    _lastBootstrapSignature = signature;
                    _lastBootstrapReason = reason;
                }
                ScheduleBootstrapResponseTimeoutWatch(stanzaId);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[AppStateSync] App-state key bootstrap request failed: {ex.Message}");
                ScheduleBootstrapRetry();
            }
        }

        private async Task RequestAppStateSyncKeysByIdAsync(IEnumerable<string> keyIds, string reason)
        {
            var now = DateTime.UtcNow;
            if (now - _lastKeyBootstrapRequestUtc < KeyBootstrapCooldown)
            {
                return;
            }

            var missingKeyIds = new List<byte[]>();
            var missingKeyIdSet = new HashSet<string>(StringComparer.Ordinal);

            foreach (var keyId in keyIds ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(keyId))
                {
                    continue;
                }

                byte[] keyBytes;
                try
                {
                    keyBytes = Convert.FromBase64String(keyId);
                }
                catch
                {
                    WhatsAppService.Log($"[AppStateSync] Cannot request malformed app-state key id '{keyId}' (reason={reason})");
                    continue;
                }

                var existing = await _keyStore.GetAppStateSyncKeyAsync(keyId);
                if (existing != null)
                {
                    continue;
                }

                if (missingKeyIdSet.Add(keyId))
                {
                    missingKeyIds.Add(keyBytes);
                }
            }

            if (missingKeyIds.Count == 0)
            {
                return;
            }

            string signature = string.Join(",", missingKeyIdSet.OrderBy(k => k, StringComparer.Ordinal));
            lock (_bootstrapLock)
            {
                if (!string.IsNullOrEmpty(signature) &&
                    string.Equals(_suppressedBootstrapSignature, signature, StringComparison.Ordinal))
                {
                    WhatsAppService.Log($"[AppStateSync] Waiting for previously requested app-state key share: {signature}");
                    return;
                }
            }

            _lastKeyBootstrapRequestUtc = now;
            try
            {
                WhatsAppService.Log($"[AppStateSync] Requesting app-state key bootstrap (reason={reason}, missingIds={missingKeyIds.Count}): {signature}");
                string stanzaId = await _socket.RequestAppStateSyncKeyShareAsync(missingKeyIds);
                lock (_bootstrapLock)
                {
                    _pendingBootstrapRequestIds.Add(stanzaId);
                    _lastBootstrapSignature = signature;
                    _lastBootstrapReason = reason;
                }
                ScheduleBootstrapResponseTimeoutWatch(stanzaId);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[AppStateSync] App-state key bootstrap request failed: {ex.Message}");
                ScheduleBootstrapRetry();
            }
        }

        private async Task RequestFatalRecoveryAsync(string collectionName, long timestamp, string reason)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return;
            }

            var now = DateTime.UtcNow;
            lock (_fatalRecoveryLock)
            {
                if (_fatalRecoveryCollectionsInFlight.Contains(collectionName))
                {
                    WhatsAppService.Log($"[AppStateSync] Fatal recovery already in flight for {collectionName} (reason={reason})");
                    return;
                }

                if (_lastFatalRecoveryRequestUtcByCollection.TryGetValue(collectionName, out var lastRequestUtc) &&
                    now - lastRequestUtc < FatalRecoveryCooldown)
                {
                    WhatsAppService.Log($"[AppStateSync] Fatal recovery suppressed by cooldown for {collectionName} (reason={reason})");
                    return;
                }

                _fatalRecoveryCollectionsInFlight.Add(collectionName);
                _lastFatalRecoveryRequestUtcByCollection[collectionName] = now;
            }

            try
            {
                string stanzaId = await _socket.RequestSyncdSnapshotFatalRecoveryAsync(collectionName, timestamp);
                lock (_fatalRecoveryLock)
                {
                    _pendingFatalRecoveryByStanzaId[stanzaId] = collectionName;
                }

                WhatsAppService.Log($"[AppStateSync] Requested fatal snapshot recovery: collection={collectionName}, timestamp={timestamp}, stanzaId={stanzaId}, reason={reason}");
                ScheduleFatalRecoveryTimeoutWatch(stanzaId, collectionName, reason);
            }
            catch (Exception ex)
            {
                lock (_fatalRecoveryLock)
                {
                    _fatalRecoveryCollectionsInFlight.Remove(collectionName);
                    _lastFatalRecoveryRequestUtcByCollection.Remove(collectionName);
                }

                WhatsAppService.Log($"[AppStateSync] Fatal snapshot recovery request failed for {collectionName}: {ex.Message}");
            }
        }

        private void ScheduleFatalRecoveryTimeoutWatch(string stanzaId, string collectionName, string reason)
        {
            if (string.IsNullOrWhiteSpace(stanzaId) || string.IsNullOrWhiteSpace(collectionName))
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(35));

                    bool stillPending = false;
                    lock (_fatalRecoveryLock)
                    {
                        stillPending = _pendingFatalRecoveryByStanzaId.ContainsKey(stanzaId);
                        if (stillPending)
                        {
                            _pendingFatalRecoveryByStanzaId.Remove(stanzaId);
                            _fatalRecoveryCollectionsInFlight.Remove(collectionName);
                        }
                    }

                    if (stillPending)
                    {
                        WhatsAppService.Log($"[AppStateSync] Fatal snapshot recovery timed out: collection={collectionName}, stanzaId={stanzaId}, reason={reason}");
                        await EnsureKeyBootstrapRequestedAsync($"fatal-recovery-timeout:{collectionName}", new[] { collectionName });
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[AppStateSync] Fatal recovery timeout watch failed for {collectionName}: {ex.Message}");
                }
            });
        }

        private async Task ApplyRecoveredSnapshotAsync(string collectionName, SyncdSnapshot snapshot, string reason)
        {
            if (snapshot == null)
            {
                return;
            }

            var previousState = await _keyStore.GetAppStateCollectionStateAsync(collectionName) ?? NewState(collectionName);
            long previousVersion = previousState.Version;
            long snapshotVersion = snapshot.Version != null ? unchecked((long)snapshot.Version.Version) : 0;
            var snapshotResult = await DecodeSnapshotAsync(collectionName, snapshot, previousVersion);
            await _keyStore.SetAppStateCollectionStateAsync(collectionName, snapshotResult.State);
            await ApplyMutationsAsync(collectionName, snapshotResult.MutationMap, false);

            if (DefaultCollections.Contains(collectionName, StringComparer.Ordinal))
            {
                _pendingInitialSync = false;
            }

            WhatsAppService.Log($"[AppStateSync] Fatal recovery snapshot applied for {collectionName}: v{previousVersion}->v{snapshotResult.State.Version}, snapshotVersion={snapshotVersion}, mutations={snapshotResult.MutationMap.Count}, reason={reason}");
        }

        private static byte[] DecompressFatalRecoverySnapshot(byte[] bytes)
        {
            try
            {
                return CryptoUtils.DecompressZlib(bytes);
            }
            catch
            {
                return bytes;
            }
        }

        private void ScheduleBootstrapRetry()
        {
            if (Interlocked.Exchange(ref _bootstrapRetryScheduled, 1) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(20));
                    if (_pendingInitialSync)
                    {
                        WhatsAppService.Log("[AppStateSync] Retrying app-state key bootstrap after delay");
                        await EnsureCollectionsResyncedAsync(DefaultCollections, !_initialSyncComplete, "key-bootstrap-retry");
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[AppStateSync] Delayed key bootstrap retry failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _bootstrapRetryScheduled, 0);
                }
            });
        }

        private void ScheduleBootstrapResponseTimeoutWatch(string stanzaId)
        {
            if (string.IsNullOrWhiteSpace(stanzaId))
            {
                return;
            }

            if (Interlocked.Exchange(ref _bootstrapResponseWatchScheduled, 1) != 0)
            {
                return;
            }

            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(25));

                    bool stillPending;
                    lock (_bootstrapLock)
                    {
                        stillPending = _pendingBootstrapRequestIds.Contains(stanzaId);
                    }

                    var allKeys = await _keyStore.GetAllAppStateSyncKeysAsync();
                    if (_pendingInitialSync && !HasAllBootstrapKeyIds(allKeys))
                    {
                        if (stillPending)
                        {
                            lock (_bootstrapLock)
                            {
                                _pendingBootstrapRequestIds.Remove(stanzaId);
                            }
                        }

                        string signature;
                        lock (_bootstrapLock)
                        {
                            signature = _lastBootstrapSignature;
                            _suppressedBootstrapSignature = signature;
                        }

                        WhatsAppService.Log($"[AppStateSync] App-state key bootstrap timed out or incomplete; parked until key share/dirty/reconnect: stanzaId={stanzaId}, reason={_lastBootstrapReason}, missing={signature}, keys={(allKeys?.Count ?? 0)}");
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[AppStateSync] Key bootstrap response watch failed: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _bootstrapResponseWatchScheduled, 0);
                }
            });
        }

        private void ResetBootstrapSessionState()
        {
            lock (_bootstrapLock)
            {
                _pendingBootstrapRequestIds.Clear();
                _lastBootstrapSignature = null;
                _suppressedBootstrapSignature = null;
            }
            _lastKeyBootstrapRequestUtc = DateTime.MinValue;
        }

        private async Task<List<byte[]>> ProbeAppStateKeyIdsAsync(IEnumerable<string> collections)
        {
            var requested = collections?.Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.Ordinal).ToList() ?? new List<string>();
            if (requested.Count == 0)
            {
                return new List<byte[]>();
            }

            try
            {
                var chunks = await QueryCollectionsAsync(requested);
                var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
                foreach (var chunk in chunks.Values)
                {
                    AddKeyId(chunk?.Snapshot?.KeyId?.Id?.ToByteArray(), keys);
                    if (chunk?.Patches != null)
                    {
                        foreach (var patch in chunk.Patches)
                        {
                            AddKeyId(patch?.KeyId?.Id?.ToByteArray(), keys);
                        }
                    }
                }

                if (keys.Count > 0)
                {
                    WhatsAppService.Log($"[AppStateSync] Probed {keys.Count} app-state key id(s) from collection headers: {string.Join(",", keys.Keys)}");
                }
                else
                {
                    WhatsAppService.Log("[AppStateSync] No app-state key ids were exposed in collection headers");
                }

                return keys.Values.ToList();
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[AppStateSync] Failed to probe app-state key ids: {ex.Message}");
                return new List<byte[]>();
            }
        }

        private static bool TryExtractMissingAppStateSyncKeyId(Exception ex, out string keyId)
        {
            keyId = null;
            string message = ex?.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return false;
            }

            const string prefix = "Missing app-state sync key ";
            int prefixIndex = message.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (prefixIndex < 0)
            {
                return false;
            }

            string candidate = message.Substring(prefixIndex + prefix.Length).Trim();
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }

            keyId = candidate;
            return true;
        }

        private bool HasAllBootstrapKeyIds(Dictionary<string, Proto.Message.Types.AppStateSyncKeyData> keys)
        {
            string signature;
            lock (_bootstrapLock)
            {
                signature = _lastBootstrapSignature;
            }

            if (string.IsNullOrWhiteSpace(signature))
            {
                return keys != null && keys.Count > 0;
            }

            if (keys == null || keys.Count == 0)
            {
                return false;
            }

            foreach (var entry in signature.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = entry?.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (!keys.ContainsKey(trimmed))
                {
                    return false;
                }
            }

            return true;
        }

        private static void AddKeyId(byte[] keyId, IDictionary<string, byte[]> target)
        {
            if (keyId == null || keyId.Length == 0)
            {
                return;
            }

            string encoded = Convert.ToBase64String(keyId);
            if (!target.ContainsKey(encoded))
            {
                target[encoded] = keyId;
            }
        }

        private static List<string> ExpandCollections(string collectionName)
        {
            if (string.IsNullOrWhiteSpace(collectionName))
            {
                return new List<string>();
            }

            switch (collectionName.Trim().ToLowerInvariant())
            {
                case "regular":
                    return new List<string> { "regular_high", "regular_low", "regular" };
                case "critical_block":
                    return new List<string> { "critical_block" };
                case "critical_unblock_low":
                    return new List<string> { "critical_unblock_low" };
                default:
                    return new List<string> { collectionName };
            }
        }

        private async Task<Dictionary<string, CollectionChunk>> QueryCollectionsAsync(IEnumerable<string> collections, HashSet<string> forceSnapshotCollections = null)
        {
            var requested = collections.Distinct(StringComparer.Ordinal).ToList();
            var collectionNodes = new List<BinaryNode>();
            foreach (var name in requested)
            {
                var state = await _keyStore.GetAppStateCollectionStateAsync(name) ?? NewState(name);
                bool forceSnapshot = forceSnapshotCollections != null && forceSnapshotCollections.Remove(name);
                long queryVersion = forceSnapshot ? 0 : state.Version;
                collectionNodes.Add(new BinaryNode("collection", new Dictionary<string, string>
                {
                    { "name", name },
                    { "version", queryVersion.ToString() },
                    { "return_snapshot", (forceSnapshot || state.Version == 0).ToString().ToLowerInvariant() }
                }));
                if (forceSnapshot)
                {
                    WhatsAppService.Log($"[AppStateSync] Querying {name} from v{state.Version} with forced snapshot (wireVersion={queryVersion})");
                }
            }

            var iq = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "xmlns", "w:sync:app:state" },
                { "type", "set" }
            }, new List<BinaryNode>
            {
                new BinaryNode("sync", null, collectionNodes)
            });

            var result = await _socket.QueryAsync(iq, 60000);
            var syncNode = result?.GetChild("sync") ?? result?.FindDescendant("sync");
            var output = new Dictionary<string, CollectionChunk>(StringComparer.Ordinal);
            foreach (var collectionNode in syncNode?.GetChildren("collection") ?? new List<BinaryNode>())
            {
                string name = collectionNode.Attrs?.GetDictionaryValueOrDefault("name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var chunk = new CollectionChunk
                {
                    Name = name,
                    HasMorePatches = string.Equals(collectionNode.Attrs?.GetDictionaryValueOrDefault("has_more_patches"), "true", StringComparison.OrdinalIgnoreCase)
                };

                var snapshotNode = collectionNode.GetChild("snapshot");
                if (snapshotNode != null && snapshotNode.GetContentBytes() != null)
                {
                    chunk.Snapshot = await ParseSnapshotNodeAsync(snapshotNode);
                }

                var patchesNode = collectionNode.GetChild("patches");
                foreach (var patchNode in (patchesNode ?? collectionNode).GetChildren("patch"))
                {
                    var patchBytes = patchNode.GetContentBytes();
                    if (patchBytes == null || patchBytes.Length == 0)
                    {
                        continue;
                    }

                    var patch = SyncdPatch.Parser.ParseFrom(patchBytes);
                    if (patch.Version == null)
                    {
                        long currentVersion = 0;
                        long.TryParse(collectionNode.Attrs?.GetDictionaryValueOrDefault("version") ?? "0", out currentVersion);
                        patch.Version = new SyncdVersion { Version = (ulong)Math.Max(0, currentVersion + 1) };
                    }

                    chunk.Patches.Add(patch);
                }

                output[name] = chunk;
                WhatsAppService.Log($"[AppStateSync] Query result {name}: snapshot={(chunk.Snapshot != null)}, patches={chunk.Patches.Count}, hasMore={chunk.HasMorePatches}");
            }

            return output;
        }

        private async Task<SyncdSnapshot> ParseSnapshotNodeAsync(BinaryNode snapshotNode)
        {
            var bytes = snapshotNode.GetContentBytes();
            if (bytes == null || bytes.Length == 0)
            {
                return null;
            }

            try
            {
                var blobRef = ExternalBlobReference.Parser.ParseFrom(bytes);
                var data = await DownloadExternalBlobAsync(blobRef);
                return SyncdSnapshot.Parser.ParseFrom(data);
            }
            catch
            {
                return SyncdSnapshot.Parser.ParseFrom(bytes);
            }
        }

        private async Task<byte[]> DownloadExternalBlobAsync(ExternalBlobReference blob)
        {
            if (blob == null || !blob.HasMediaKey)
            {
                throw new InvalidOperationException("App-state external blob is missing mediaKey");
            }

            byte[] expectedSha = blob.HasFileEncSha256 ? blob.FileEncSha256.ToByteArray() : null;
            return await _socket.DownloadAndDecryptMediaAsync(null, blob.DirectPath, blob.MediaKey.ToByteArray(), "md-app-state", expectedSha);
        }

        private async Task<List<SyncdMutation>> DownloadExternalMutationsAsync(ExternalBlobReference blob)
        {
            var data = await DownloadExternalBlobAsync(blob);
            var mutations = SyncdMutations.Parser.ParseFrom(data);
            return mutations?.Mutations?.ToList() ?? new List<SyncdMutation>();
        }

        private async Task<CollectionDecodeResult> DecodeSnapshotAsync(string name, SyncdSnapshot snapshot, long minimumVersionNumber)
        {
            long version = snapshot?.Version != null ? unchecked((long)snapshot.Version.Version) : 0;
            var initial = NewState(name);
            initial.Version = version;
            bool shouldCollect = version > minimumVersionNumber;
            var envelopes = snapshot.Records.Select(record => new MutationEnvelope
            {
                Operation = SyncdMutation.Types.SyncdOperation.Set,
                Record = record
            });

            var decoded = await DecodeSyncdMutationsAsync(name, envelopes, initial, shouldCollect);
            var state = new AppStateCollectionState
            {
                Name = name,
                Version = version,
                Hash = decoded.Hash,
                IndexValueMap = decoded.IndexValueMap
            };

            await VerifySnapshotMacAsync(name, snapshot, state);
            return new CollectionDecodeResult
            {
                State = state,
                MutationMap = decoded.MutationMap
            };
        }

        private async Task<CollectionDecodeResult> DecodePatchesAsync(string name, IEnumerable<SyncdPatch> patches, AppStateCollectionState initialState, long minimumVersionNumber)
        {
            var currentState = CloneState(initialState);
            var mutationMap = new Dictionary<string, DecodedMutation>(StringComparer.Ordinal);
            foreach (var patch in patches.OrderBy(p => p?.Version != null ? unchecked((long)p.Version.Version) : 0))
            {
                if (patch == null)
                {
                    continue;
                }

                await VerifyPatchMacAsync(name, patch);
                var envelopes = new List<MutationEnvelope>();
                foreach (var mutation in patch.Mutations ?? new Google.Protobuf.Collections.RepeatedField<SyncdMutation>())
                {
                    if (mutation?.Record != null)
                    {
                        envelopes.Add(new MutationEnvelope
                        {
                            Operation = mutation.Operation,
                            Record = mutation.Record
                        });
                    }
                }

                if (patch.ExternalMutations != null)
                {
                    foreach (var mutation in await DownloadExternalMutationsAsync(patch.ExternalMutations))
                    {
                        if (mutation?.Record != null)
                        {
                            envelopes.Add(new MutationEnvelope
                            {
                                Operation = mutation.Operation,
                                Record = mutation.Record
                            });
                        }
                    }
                }

                bool shouldCollect = patch.Version != null && unchecked((long)patch.Version.Version) > minimumVersionNumber;
                var decoded = await DecodeSyncdMutationsAsync(name, envelopes, currentState, shouldCollect);
                currentState = new AppStateCollectionState
                {
                    Name = name,
                    Version = patch.Version != null ? unchecked((long)patch.Version.Version) : currentState.Version,
                    Hash = decoded.Hash,
                    IndexValueMap = decoded.IndexValueMap
                };
                await VerifyPatchSnapshotMacAsync(name, patch, currentState);

                MergeMutationMaps(mutationMap, decoded.MutationMap);
            }

            return new CollectionDecodeResult
            {
                State = currentState,
                MutationMap = mutationMap
            };
        }

        private async Task<DecodedMutationsResult> DecodeSyncdMutationsAsync(string name, IEnumerable<MutationEnvelope> envelopes, AppStateCollectionState initialState, bool collectMutations)
        {
            var accumulator = new LtHashAccumulator(name, initialState);
            var result = new DecodedMutationsResult();
            var mutationMap = new Dictionary<string, DecodedMutation>(StringComparer.Ordinal);

            foreach (var envelope in envelopes ?? Enumerable.Empty<MutationEnvelope>())
            {
                var record = envelope?.Record;
                if (record?.Index == null)
                {
                    continue;
                }

                byte[] keyIdBytes = record.KeyId?.Id?.ToByteArray();
                string keyId = keyIdBytes != null && keyIdBytes.Length > 0
                    ? Convert.ToBase64String(keyIdBytes)
                    : _authState.MyAppStateKeyId;
                var keys = await GetMutationKeysAsync(keyId);

                byte[] indexBytes = record.Index.Blob?.ToByteArray() ?? new byte[0];
                byte[] valueBlob = record.Value?.Blob?.ToByteArray() ?? new byte[0];
                byte[] valueMac = valueBlob.Length >= 32 ? valueBlob.Skip(valueBlob.Length - 32).ToArray() : null;
                byte[] encryptedContent = valueBlob.Length >= 32 ? valueBlob.Take(valueBlob.Length - 32).ToArray() : new byte[0];
                if (encryptedContent.Length < 16 || valueMac == null)
                {
                    throw new InvalidOperationException($"App-state mutation for {name} is missing encrypted content");
                }

                byte[] expectedValueMac = GenerateMac(envelope.Operation, encryptedContent, keyIdBytes ?? new byte[0], keys.ValueMacKey);
                if (!ByteArraysEqual(expectedValueMac, valueMac))
                {
                    throw new InvalidOperationException($"App-state content MAC verification failed for {name}");
                }

                byte[] iv = encryptedContent.Take(16).ToArray();
                byte[] cipher = encryptedContent.Skip(16).ToArray();
                byte[] decrypted = CryptoUtils.AesCbcDecrypt(cipher, keys.ValueEncryptionKey, iv);
                SyncActionData syncAction = SyncActionData.Parser.ParseFrom(decrypted);

                byte[] expectedIndexMac = CryptoUtils.HmacSha256(syncAction.Index.ToByteArray(), keys.IndexKey);
                if (!ByteArraysEqual(expectedIndexMac, indexBytes))
                {
                    throw new InvalidOperationException($"App-state index MAC verification failed for {name}");
                }

                JArray index = ParseIndex(syncAction.Index.ToByteArray());

                accumulator.Mix(indexBytes, valueMac ?? new byte[0], envelope.Operation);

                if (collectMutations)
                {
                    string mutationKey = Convert.ToBase64String(indexBytes);
                    mutationMap[mutationKey] = new DecodedMutation
                    {
                        SyncAction = syncAction,
                        Index = index,
                        Operation = envelope.Operation
                    };
                }
            }

            var finished = accumulator.Finish(name, initialState?.Version ?? 0);
            result.Hash = finished.Hash;
            result.IndexValueMap = finished.IndexValueMap;
            result.MutationMap = mutationMap;
            return result;
        }

        private async Task ApplyMutationsAsync(string collectionName, Dictionary<string, DecodedMutation> mutations, bool isInitialSync)
        {
            if (mutations == null || mutations.Count == 0)
            {
                WhatsAppService.Log($"[AppStateSync] {collectionName}: no applicable mutations");
                return;
            }

            int applied = 0;
            foreach (var mutation in mutations.Values)
            {
                WhatsAppService.Log($"[AppStateSync] {collectionName}: mutation candidate index={mutation?.Index} operation={mutation?.Operation} summary={DescribeAction(mutation?.SyncAction?.Value)}");
                if (await ApplyMutationAsync(collectionName, mutation, isInitialSync))
                {
                    applied++;
                }
            }

            if (applied > 0)
            {
                _owner.SchedulePersistForAppState($"app-state:{collectionName}:{applied}");
            }

            WhatsAppService.Log($"[AppStateSync] {collectionName}: applied {applied}/{mutations.Count} mutation(s)");
        }

        private async Task<bool> ApplyMutationAsync(string collectionName, DecodedMutation mutation, bool isInitialSync)
        {
            if (mutation == null)
            {
                return false;
            }

            string chatJid = ExtractChatJid(mutation.Index);
            string messageId = ExtractMessageId(mutation.Index);
            var value = mutation.SyncAction?.Value;

            if (mutation.Operation == SyncdMutation.Types.SyncdOperation.Remove)
            {
                if (!string.IsNullOrEmpty(chatJid) && !string.IsNullOrEmpty(messageId))
                {
                    return await _owner.ApplyAppStateDeleteMessageAsync(chatJid, messageId);
                }

                return false;
            }

            if (value == null)
            {
                WhatsAppService.Log($"[AppStateSync] {collectionName}: mutation has no value index={mutation.Index} operation={mutation.Operation}");
                return false;
            }

            if (value.ContactAction != null)
            {
                string preferredJid = FirstNonEmpty(value.ContactAction.PnJid, value.ContactAction.LidJid, chatJid);
                string name = FirstNonEmpty(value.ContactAction.FullName, value.ContactAction.FirstName, value.ContactAction.Username);
                if (!string.IsNullOrWhiteSpace(value.ContactAction.LidJid) && !string.IsNullOrWhiteSpace(value.ContactAction.PnJid))
                {
                    _owner.RegisterAliasFromAppState(value.ContactAction.LidJid, value.ContactAction.PnJid, "app-state-contact");
                }

                if (!string.IsNullOrWhiteSpace(preferredJid) && !string.IsNullOrWhiteSpace(name))
                {
                    await _owner.ApplyAppStateContactNameAsync(preferredJid, name);
                    return true;
                }
            }

            if (value.LidContactAction != null)
            {
                string preferredJid = chatJid;
                string name = FirstNonEmpty(value.LidContactAction.FullName, value.LidContactAction.FirstName, value.LidContactAction.Username);
                if (!string.IsNullOrWhiteSpace(preferredJid) && !string.IsNullOrWhiteSpace(name))
                {
                    await _owner.ApplyAppStateContactNameAsync(preferredJid, name);
                    return true;
                }
            }

            if (value.PushNameSetting != null && !string.IsNullOrWhiteSpace(value.PushNameSetting.Name))
            {
                await _owner.ApplyAppStateSelfPushNameAsync(value.PushNameSetting.Name);
                return true;
            }

            if (value.MarkChatAsReadAction != null && !string.IsNullOrWhiteSpace(chatJid))
            {
                await _owner.ApplyAppStateReadStateAsync(chatJid, value.MarkChatAsReadAction.Read);
                return true;
            }

            if (value.DeleteChatAction != null && !string.IsNullOrWhiteSpace(chatJid))
            {
                await _owner.ApplyAppStateDeleteChatAsync(chatJid);
                return true;
            }

            if (value.DeleteMessageForMeAction != null && !string.IsNullOrWhiteSpace(chatJid) && !string.IsNullOrWhiteSpace(messageId))
            {
                return await _owner.ApplyAppStateDeleteMessageAsync(chatJid, messageId);
            }

            if (value.ArchiveChatAction != null && !string.IsNullOrWhiteSpace(chatJid))
            {
                await _owner.ApplyAppStateChatFlagsAsync(chatJid, archived: value.ArchiveChatAction.Archived);
                return true;
            }

            if (value.PinAction != null && !string.IsNullOrWhiteSpace(chatJid))
            {
                long? pinTimestamp = value.PinAction.Pinned
                    ? (long?)(value.Timestamp > 0 ? value.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
                    : null;
                await _owner.ApplyAppStateChatFlagsAsync(
                    chatJid,
                    pinned: value.PinAction.Pinned,
                    pinnedTimestamp: pinTimestamp);
                return true;
            }

            if (value.MuteAction != null && !string.IsNullOrWhiteSpace(chatJid))
            {
                long? muteEnd = value.MuteAction.Muted ? (long?)value.MuteAction.MuteEndTimestamp : null;
                await _owner.ApplyAppStateChatFlagsAsync(chatJid, muteEndTimestamp: muteEnd, applyMute: true);
                return true;
            }

            if (value.PnForLidChatAction != null && !string.IsNullOrWhiteSpace(chatJid) && !string.IsNullOrWhiteSpace(value.PnForLidChatAction.PnJid))
            {
                _owner.RegisterAliasFromAppState(chatJid, value.PnForLidChatAction.PnJid, "app-state-pnforlid");
                return true;
            }

            WhatsAppService.Log($"[AppStateSync] {collectionName}: unsupported mutation index={mutation.Index} summary={DescribeAction(value)} operation={mutation.Operation}");
            return false;
        }

        private static string DescribeAction(SyncActionValue value)
        {
            if (value == null)
            {
                return "null";
            }

            if (value.ContactAction != null) return "ContactAction";
            if (value.LidContactAction != null) return "LidContactAction";
            if (value.PushNameSetting != null) return "PushNameSetting";
            if (value.MarkChatAsReadAction != null) return "MarkChatAsReadAction";
            if (value.DeleteChatAction != null) return "DeleteChatAction";
            if (value.DeleteMessageForMeAction != null) return "DeleteMessageForMeAction";
            if (value.ArchiveChatAction != null) return "ArchiveChatAction";
            if (value.PinAction != null) return "PinAction";
            if (value.MuteAction != null) return "MuteAction";
            if (value.StarAction != null) return "StarAction";
            if (value.LabelAssociationAction != null) return "LabelAssociationAction";
            if (value.RecentEmojiWeightsAction != null) return "RecentEmojiWeightsAction";
            if (value.TimeFormatAction != null) return "TimeFormatAction";
            if (value.PnForLidChatAction != null) return "PnForLidChatAction";
            if (value.ClearChatAction != null) return "ClearChatAction";
            if (value.LocaleSetting != null) return "LocaleSetting";
            if (value.UnarchiveChatsSetting != null) return "UnarchiveChatsSetting";
            if (value.PrimaryFeature != null) return "PrimaryFeature";
            if (value.AndroidUnsupportedActions != null) return "AndroidUnsupportedActions";
            if (value.DeviceCapabilities != null) return "DeviceCapabilities";
            if (value.NotificationActivitySettingAction != null) return "NotificationActivitySettingAction";
            if (value.NoteEditAction != null) return "NoteEditAction";
            if (value.FavoritesAction != null) return "FavoritesAction";
            if (value.PrivateProcessingSettingAction != null) return "PrivateProcessingSettingAction";
            if (value.AiThreadRenameAction != null) return "AiThreadRenameAction";
            return "UnknownOrEmpty";
        }

        private async Task VerifySnapshotMacAsync(string name, SyncdSnapshot snapshot, AppStateCollectionState state)
        {
            if (snapshot?.Mac == null || snapshot.KeyId?.Id == null)
            {
                return;
            }

            var keys = await GetMutationKeysAsync(Convert.ToBase64String(snapshot.KeyId.Id.ToByteArray()));
            byte[] expected = GenerateSnapshotMac(state.Hash, state.Version, name, keys.SnapshotMacKey);
            byte[] actual = snapshot.Mac.ToByteArray();
            if (!ByteArraysEqual(expected, actual))
            {
                throw new InvalidOperationException($"App-state snapshot MAC verification failed for {name} at v{state.Version}");
            }
        }

        private async Task VerifyPatchMacAsync(string name, SyncdPatch patch)
        {
            if (patch?.PatchMac == null || patch.KeyId?.Id == null)
            {
                return;
            }

            var valueMacs = new List<byte[]>();
            foreach (var mutation in patch.Mutations ?? new Google.Protobuf.Collections.RepeatedField<SyncdMutation>())
            {
                var blob = mutation?.Record?.Value?.Blob?.ToByteArray();
                if (blob != null && blob.Length >= 32)
                {
                    valueMacs.Add(blob.Skip(blob.Length - 32).ToArray());
                }
            }

            var keys = await GetMutationKeysAsync(Convert.ToBase64String(patch.KeyId.Id.ToByteArray()));
            byte[] snapshotMac = patch.SnapshotMac?.ToByteArray() ?? new byte[0];
            long version = patch.Version != null ? unchecked((long)patch.Version.Version) : 0;
            byte[] expected = GeneratePatchMac(snapshotMac, valueMacs, version, name, keys.PatchMacKey);
            byte[] actual = patch.PatchMac.ToByteArray();
            if (!ByteArraysEqual(expected, actual))
            {
                throw new InvalidOperationException($"App-state patch MAC verification failed for {name} at v{version}");
            }
        }

        private async Task VerifyPatchSnapshotMacAsync(string name, SyncdPatch patch, AppStateCollectionState state)
        {
            if (patch?.SnapshotMac == null || patch.KeyId?.Id == null)
            {
                return;
            }

            var keys = await GetMutationKeysAsync(Convert.ToBase64String(patch.KeyId.Id.ToByteArray()));
            long version = patch.Version != null ? unchecked((long)patch.Version.Version) : state?.Version ?? 0;
            byte[] expected = GenerateSnapshotMac(state?.Hash ?? new byte[128], version, name, keys.SnapshotMacKey);
            byte[] actual = patch.SnapshotMac.ToByteArray();
            if (!ByteArraysEqual(expected, actual))
            {
                throw new InvalidOperationException($"App-state snapshot MAC verification failed for {name} at v{version}");
            }
        }

        private async Task<MutationKeys> GetMutationKeysAsync(string keyId)
        {
            if (string.IsNullOrWhiteSpace(keyId))
            {
                keyId = _authState.MyAppStateKeyId;
            }

            if (string.IsNullOrWhiteSpace(keyId))
            {
                throw new InvalidOperationException("No app-state key id available");
            }

            var keyData = await _keyStore.GetAppStateSyncKeyAsync(keyId);
            if (keyData == null || !keyData.HasKeyData)
            {
                throw new InvalidOperationException($"Missing app-state sync key {keyId}");
            }

            return DeriveMutationKeys(keyData.KeyData.ToByteArray());
        }

        private static MutationKeys DeriveMutationKeys(byte[] keyData)
        {
            var expanded = CryptoUtils.Hkdf(keyData ?? new byte[0], 160, null, MutationKeysInfo);
            return new MutationKeys
            {
                IndexKey = expanded.Take(32).ToArray(),
                ValueEncryptionKey = expanded.Skip(32).Take(32).ToArray(),
                ValueMacKey = expanded.Skip(64).Take(32).ToArray(),
                SnapshotMacKey = expanded.Skip(96).Take(32).ToArray(),
                PatchMacKey = expanded.Skip(128).Take(32).ToArray()
            };
        }

        private static byte[] GenerateMac(SyncdMutation.Types.SyncdOperation operation, byte[] data, byte[] keyId, byte[] key)
        {
            byte operationByte = operation == SyncdMutation.Types.SyncdOperation.Remove ? (byte)0x02 : (byte)0x01;
            byte[] keyData = keyId ?? new byte[0];
            byte[] prefix = new byte[1 + keyData.Length];
            prefix[0] = operationByte;
            Array.Copy(keyData, 0, prefix, 1, keyData.Length);
            byte[] suffix = new byte[8];
            suffix[7] = (byte)prefix.Length;
            byte[] total = Combine(prefix, data ?? new byte[0], suffix);
            return CryptoUtils.HmacSha512(total, key ?? new byte[0]).Take(32).ToArray();
        }

        private static byte[] GenerateSnapshotMac(byte[] ltHash, long version, string name, byte[] key)
        {
            byte[] total = Combine(ltHash ?? new byte[128], To64BitNetworkOrder(version), Encoding.UTF8.GetBytes(name ?? string.Empty));
            return CryptoUtils.HmacSha256(total, key ?? new byte[0]);
        }

        private static byte[] GeneratePatchMac(byte[] snapshotMac, IEnumerable<byte[]> valueMacs, long version, string name, byte[] key)
        {
            var parts = new List<byte[]> { snapshotMac ?? new byte[0] };
            if (valueMacs != null)
            {
                parts.AddRange(valueMacs.Where(v => v != null));
            }

            parts.Add(To64BitNetworkOrder(version));
            parts.Add(Encoding.UTF8.GetBytes(name ?? string.Empty));
            return CryptoUtils.HmacSha256(Combine(parts.ToArray()), key ?? new byte[0]);
        }

        private static byte[] To64BitNetworkOrder(long value)
        {
            byte[] output = new byte[8];
            uint lower = unchecked((uint)value);
            output[4] = (byte)((lower >> 24) & 0xFF);
            output[5] = (byte)((lower >> 16) & 0xFF);
            output[6] = (byte)((lower >> 8) & 0xFF);
            output[7] = (byte)(lower & 0xFF);
            return output;
        }

        private static JArray ParseIndex(byte[] indexBytes)
        {
            if (indexBytes == null || indexBytes.Length == 0)
            {
                return new JArray();
            }

            string text = Encoding.UTF8.GetString(indexBytes);
            try
            {
                var token = JToken.Parse(text);
                if (token is JArray array)
                {
                    return array;
                }
            }
            catch
            {
            }

            return new JArray(text);
        }

        private static string ExtractChatJid(JArray index)
        {
            if (index == null)
            {
                return null;
            }

            foreach (var token in index)
            {
                var text = token?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && text.Contains("@"))
                {
                    return text;
                }
            }

            return null;
        }

        private static string ExtractMessageId(JArray index)
        {
            if (index == null)
            {
                return null;
            }

            for (int i = index.Count - 1; i >= 0; i--)
            {
                var text = index[i]?.ToString();
                if (!string.IsNullOrWhiteSpace(text) && !text.Contains("@") && text.Length >= 6)
                {
                    return text;
                }
            }

            return null;
        }

        private static void MergeMutationMaps(Dictionary<string, DecodedMutation> target, Dictionary<string, DecodedMutation> source)
        {
            if (target == null || source == null)
            {
                return;
            }

            foreach (var kvp in source)
            {
                target[kvp.Key] = kvp.Value;
            }
        }

        private static AppStateCollectionState NewState(string name)
        {
            return new AppStateCollectionState
            {
                Name = name,
                Version = 0,
                Hash = new byte[128],
                IndexValueMap = new Dictionary<string, byte[]>(StringComparer.Ordinal)
            };
        }

        private static AppStateCollectionState CloneState(AppStateCollectionState state)
        {
            if (state == null)
            {
                return null;
            }

            return new AppStateCollectionState
            {
                Name = state.Name,
                Version = state.Version,
                Hash = state.Hash != null ? (byte[])state.Hash.Clone() : new byte[128],
                IndexValueMap = state.IndexValueMap != null
                    ? state.IndexValueMap.ToDictionary(kvp => kvp.Key, kvp => kvp.Value != null ? (byte[])kvp.Value.Clone() : null, StringComparer.Ordinal)
                    : new Dictionary<string, byte[]>(StringComparer.Ordinal)
            };
        }

        private static string FirstNonEmpty(params string[] values)
        {
            return values?.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        }

        private static bool ByteArraysEqual(byte[] left, byte[] right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] Combine(params byte[][] parts)
        {
            int total = 0;
            foreach (var part in parts)
            {
                total += part?.Length ?? 0;
            }

            byte[] output = new byte[total];
            int offset = 0;
            foreach (var part in parts)
            {
                if (part == null || part.Length == 0)
                {
                    continue;
                }

                System.Buffer.BlockCopy(part, 0, output, offset, part.Length);
                offset += part.Length;
            }

            return output;
        }
    }
}
