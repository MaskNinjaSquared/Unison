// =============================================================================
// LidMappingStore
//
// The one place that knows which LID belongs to which phone number.
//
// Today Unison spreads this across a JidAlias dictionary in WhatsAppService, a
// second copy inside SocketClient, and a json sidecar that only persists aliases
// whose chat is already on screen. The result is the bug everyone sees: the same
// person shows up twice, once per address space. This store replaces all three
// with the Baileys model - pairs are keyed by user part only, both directions
// are always written, and the device suffix is re-attached on read.
//
// Three things it deliberately keeps from rc14:
//   - a memory cache in front of storage, because these lookups sit on the
//     decrypt and send paths;
//   - request coalescing, so ten chats resolving at once make one query;
//   - a usync fallback, so an unknown phone number can still be resolved.
//
// One deliberate deviation: rc14 writes the device suffix even when it is zero
// when going LID -> PN, producing "user:0@s.whatsapp.net". Unison compares JIDs
// against chat ids that never carry ":0", so device zero is omitted in both
// directions here.
//
// Ports: rc14 src/Signal/lid-mapping.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Utils;
using Unison.Socket.WABinary;

namespace Unison.Socket.Signal
{
    public sealed class LidMappingStore
    {
        private const string ReverseKeySuffix = "_reverse";

        private static readonly TimeSpan CacheLifetime = TimeSpan.FromDays(3);

        private const int DefaultMemoryCacheMaxEntries = 1024;

        private readonly ILidMappingStorage _storage;
        private readonly ISocketLog _log;
        private readonly ILidMappingPersistPolicy _policy;
        private readonly TtlCache<string> _cache;

        private readonly object _inflightGate = new object();
        private readonly object _pendingGate = new object();

        private readonly Dictionary<string, Task<IReadOnlyList<LidMapping>>> _inflightLidLookups =
            new Dictionary<string, Task<IReadOnlyList<LidMapping>>>(StringComparer.Ordinal);

        private readonly Dictionary<string, Task<IReadOnlyList<LidMapping>>> _inflightPnLookups =
            new Dictionary<string, Task<IReadOnlyList<LidMapping>>>(StringComparer.Ordinal);

        /// <summary>PN user → LID user waiting for the next disk flush.</summary>
        private readonly Dictionary<string, string> _pendingDisk =
            new Dictionary<string, string>(StringComparer.Ordinal);

        private bool _diskPersistSuppressed;
        private readonly object _flushGate = new object();
        private Task _flushInFlight = Task.CompletedTask;

        public LidMappingStore(
            ILidMappingStorage storage,
            ISocketLog log = null,
            ILidMappingPersistPolicy policy = null)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            _storage = storage;
            _log = log ?? NullSocketLog.Instance;
            _policy = policy;
            int maxEntries = policy != null && policy.MemoryCacheMaxEntries > 0
                ? policy.MemoryCacheMaxEntries
                : DefaultMemoryCacheMaxEntries;
            // refreshOnRead: hits become MRU so actively used pairs survive the tier cap.
            _cache = new TtlCache<string>(CacheLifetime, maxEntries, refreshOnRead: true);
            // Write-behind starts held so history sync owns SQLite; host Releases after quiet-finalize.
            _diskPersistSuppressed = UsesWriteBehind;
        }

        /// <summary>
        /// Host arms a debounce of this many ms. Null while tests use immediate write-through.
        /// </summary>
        public Action<int> ArmDebounce { get; set; }

        /// <summary>True when a policy with positive batch and delay is driving write-behind.</summary>
        private bool UsesWriteBehind =>
            _policy != null && _policy.BatchSize > 0 && _policy.DelayMs > 0;

        /// <summary>
        /// Asks the server for the LIDs of phone numbers we have never seen. Set by the host once
        /// a connection exists; while it is null an unknown number simply stays unresolved.
        /// </summary>
        public Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> PnToLidResolver { get; set; }

        /// <summary>
        /// Records pairs, ignoring anything that is not one LID and one phone number, and anything
        /// already stored. Only the user parts are persisted; devices are re-attached on read.
        /// </summary>
        public async Task StoreMappingsAsync(IEnumerable<LidMapping> pairs)
        {
            if (pairs == null)
            {
                return;
            }

            var validated = new List<KeyValuePair<string, string>>();
            foreach (var pair in pairs)
            {
                if (pair == null)
                {
                    continue;
                }

                var lid = pair.Lid;
                var pn = pair.Pn;

                // Hosted and plain share one user-part key; device/server are re-attached on read.
                var straight = JidUtils.IsAnyLid(lid) && JidUtils.IsAnyPn(pn);
                var swapped = JidUtils.IsAnyPn(lid) && JidUtils.IsAnyLid(pn);
                if (!straight && !swapped)
                {
                    _log.Warn("[LidMapping] Invalid LID-PN mapping: " + lid + ", " + pn);
                    continue;
                }

                // Callers occasionally hand the pair over with the ends swapped; accept it rather
                // than dropping a mapping we could have kept.
                var lidJid = straight ? lid : pn;
                var pnJid = straight ? pn : lid;

                var lidUser = JidUtils.GetUser(lidJid);
                var pnUser = JidUtils.GetUser(pnJid);
                if (lidUser == null || pnUser == null)
                {
                    continue;
                }

                validated.Add(new KeyValuePair<string, string>(pnUser, lidUser));
            }

            if (validated.Count == 0)
            {
                return;
            }

            var known = await ReadExistingLidUsersAsync(validated.Select(p => p.Key)).ConfigureAwait(false);

            var toWrite = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in validated)
            {
                if (known.TryGetValue(pair.Key, out var existingLidUser) &&
                    string.Equals(existingLidUser, pair.Value, StringComparison.Ordinal))
                {
                    continue;
                }

                toWrite[pair.Key] = pair.Value;
            }

            if (toWrite.Count == 0)
            {
                return;
            }

            if (!UsesWriteBehind)
            {
                await WriteThroughAsync(toWrite).ConfigureAwait(false);
                return;
            }

            // Decrypt/send need the pair before history releases disk; cache first, flush later.
            foreach (var pair in toWrite)
            {
                CachePair(pair.Key, pair.Value);
            }

            int pendingCount;
            bool suppressed;
            lock (_pendingGate)
            {
                foreach (var pair in toWrite)
                {
                    _pendingDisk[pair.Key] = pair.Value;
                }

                pendingCount = _pendingDisk.Count;
                suppressed = _diskPersistSuppressed;
            }

            if (suppressed)
            {
                _log.Trace(
                    "[LidMapping] Queued " + toWrite.Count +
                    " mapping(s) for disk (history gate held; pending=" + pendingCount + ")");
                return;
            }

            if (pendingCount >= _policy.BatchSize)
            {
                await FlushPendingToDiskAsync().ConfigureAwait(false);
                return;
            }

            Action<int> arm = ArmDebounce;
            if (arm != null)
            {
                arm(_policy.DelayMs);
            }
            else
            {
                // No host timer yet: keep queued; Release/Flush will land them.
                _log.Trace(
                    "[LidMapping] Queued " + toWrite.Count +
                    " mapping(s) pending host debounce (pending=" + pendingCount + ")");
            }
        }

        /// <summary>
        /// Opens paced disk writes after the first history sync (or when warm decides there
        /// was no hold). Flushes debt immediately, then further stores use batch/delay.
        /// </summary>
        public Task EnableDiskPersistAsync(string reason = null)
        {
            if (!UsesWriteBehind)
            {
                return Task.CompletedTask;
            }

            bool alreadyOpen;
            lock (_pendingGate)
            {
                alreadyOpen = !_diskPersistSuppressed;
                _diskPersistSuppressed = false;
            }

            if (!alreadyOpen)
            {
                _log.Trace(
                    "[LidMapping] Disk persist released" +
                    (string.IsNullOrEmpty(reason) ? string.Empty : " (" + reason + ")"));
            }

            return FlushPendingToDiskAsync();
        }

        /// <summary>
        /// Writes every queued pair now (suspend / dispose / batch full / gate release).
        /// </summary>
        public Task FlushPendingToDiskAsync()
        {
            if (!UsesWriteBehind)
            {
                return Task.CompletedTask;
            }

            Task flush;
            lock (_flushGate)
            {
                flush = _flushInFlight = ContinueFlushAsync(_flushInFlight);
            }

            return flush;
        }

        private async Task ContinueFlushAsync(Task previous)
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch
            {
                // A prior flush failure must not block the next attempt.
            }

            Dictionary<string, string> toWrite;
            lock (_pendingGate)
            {
                if (_pendingDisk.Count == 0)
                {
                    return;
                }

                toWrite = new Dictionary<string, string>(_pendingDisk, StringComparer.Ordinal);
                _pendingDisk.Clear();
            }

            try
            {
                await WriteBatchAsync(toWrite).ConfigureAwait(false);
                _log.Trace("[LidMapping] Flushed " + toWrite.Count + " mapping(s) to disk");
            }
            catch (Exception ex)
            {
                lock (_pendingGate)
                {
                    foreach (var pair in toWrite)
                    {
                        if (!_pendingDisk.ContainsKey(pair.Key))
                        {
                            _pendingDisk[pair.Key] = pair.Value;
                        }
                    }
                }

                _log.Warn("[LidMapping] Disk flush failed: " + ex.Message);
                throw;
            }
        }

        private async Task WriteThroughAsync(Dictionary<string, string> toWrite)
        {
            await WriteBatchAsync(toWrite).ConfigureAwait(false);

            // Cache only after the write succeeded, so a failed write is retried instead of
            // being masked by a memory hit.
            foreach (var pair in toWrite)
            {
                CachePair(pair.Key, pair.Value);
            }

            _log.Trace("[LidMapping] Stored " + toWrite.Count + " mapping(s)");
        }

        private Task WriteBatchAsync(Dictionary<string, string> pnToLid)
        {
            var batch = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in pnToLid)
            {
                batch[pair.Key] = pair.Value;
                batch[pair.Value + ReverseKeySuffix] = pair.Key;
            }

            return _storage.SetAsync(batch);
        }

        public async Task<string> GetLidForPnAsync(string pn)
        {
            var results = await GetLidsForPnsAsync(new[] { pn }).ConfigureAwait(false);
            return results != null && results.Count > 0 ? results[0].Lid : null;
        }

        public async Task<string> GetPnForLidAsync(string lid)
        {
            var results = await GetPnsForLidsAsync(new[] { lid }).ConfigureAwait(false);
            return results != null && results.Count > 0 ? results[0].Pn : null;
        }

        /// <summary>
        /// Resolves phone numbers to LIDs, falling back to usync for the ones we have never seen.
        /// Returns null - not an empty list - when nothing could be resolved, matching Baileys.
        /// </summary>
        public Task<IReadOnlyList<LidMapping>> GetLidsForPnsAsync(IEnumerable<string> pns)
        {
            return CoalesceAsync(_inflightLidLookups, pns, ResolveLidsForPnsAsync);
        }

        /// <summary>Resolves LIDs back to phone numbers. Never queries the server: reverse lookups are local only.</summary>
        public Task<IReadOnlyList<LidMapping>> GetPnsForLidsAsync(IEnumerable<string> lids)
        {
            return CoalesceAsync(_inflightPnLookups, lids, ResolvePnsForLidsAsync);
        }

        /// <summary>Drops the memory cache. Storage is untouched.</summary>
        public void Close()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Seeds the memory cache from durable storage. Production tiers use
        /// <c>WarmMaxPairs == 0</c> (no boot warm on Mobile or High) — fill on demand.
        /// Pass an explicit <paramref name="maxForwardPairs"/> in tests; capped loads use
        /// <see cref="ILidMappingStorage.GetRecentForwardPairsAsync"/>.
        /// </summary>
        public async Task WarmCacheFromStorageAsync(int? maxForwardPairs = null)
        {
            int limit;
            if (maxForwardPairs.HasValue)
            {
                limit = maxForwardPairs.Value;
            }
            else if (_policy != null)
            {
                limit = _policy.WarmMaxPairs;
            }
            else
            {
                // No policy (unit tests): load everything the in-memory store holds.
                limit = -1;
            }

            if (limit == 0)
            {
                return;
            }

            IDictionary<string, string> rows = limit < 0
                ? await _storage.GetAllAsync().ConfigureAwait(false)
                : await _storage.GetRecentForwardPairsAsync(limit).ConfigureAwait(false);
            if (rows == null || rows.Count == 0)
            {
                return;
            }

            int warmed = 0;
            foreach (KeyValuePair<string, string> entry in rows)
            {
                if (string.IsNullOrEmpty(entry.Key) ||
                    string.IsNullOrEmpty(entry.Value) ||
                    entry.Key.EndsWith(ReverseKeySuffix, StringComparison.Ordinal))
                {
                    continue;
                }

                CachePair(entry.Key, entry.Value);
                warmed++;
            }

            if (warmed > 0)
            {
                _log.Trace("[LidMapping] Warmed " + warmed + " pair(s) from storage");
            }
        }

        /// <summary>
        /// Sync peek of the memory cache for a PN user part. Used by
        /// <c>JidAliasTable</c> so canonicalization can stay synchronous.
        /// </summary>
        public bool TryGetCachedLidUserForPn(string pnUser, out string lidUser)
        {
            lidUser = null;
            if (string.IsNullOrEmpty(pnUser))
            {
                return false;
            }

            lidUser = _cache.Get(PnKey(pnUser));
            return !string.IsNullOrEmpty(lidUser);
        }

        /// <summary>
        /// Sync peek of the memory cache for a LID user part. Reverse of
        /// <see cref="TryGetCachedLidUserForPn"/>.
        /// </summary>
        public bool TryGetCachedPnUserForLid(string lidUser, out string pnUser)
        {
            pnUser = null;
            if (string.IsNullOrEmpty(lidUser))
            {
                return false;
            }

            pnUser = _cache.Get(LidKey(lidUser));
            return !string.IsNullOrEmpty(pnUser);
        }

        /// <summary>
        /// Sync paired JID for a full query address: looks up by user part, then re-attaches
        /// the caller's device (and hosted server) the same way async resolve does.
        /// </summary>
        public bool TryGetCachedPairedJid(string jid, out string pairedJid)
        {
            pairedJid = null;
            if (string.IsNullOrEmpty(jid))
            {
                return false;
            }

            if (JidUtils.IsAnyLid(jid))
            {
                string lidUser = JidUtils.GetUser(jid);
                string pnUser;
                if (string.IsNullOrEmpty(lidUser) || !TryGetCachedPnUserForLid(lidUser, out pnUser))
                {
                    return false;
                }

                pairedJid = BuildPnForLidQuery(jid, pnUser);
                return !string.IsNullOrEmpty(pairedJid);
            }

            if (JidUtils.IsAnyPn(jid))
            {
                string pnUser = JidUtils.GetUser(jid);
                string lidUser;
                if (string.IsNullOrEmpty(pnUser) || !TryGetCachedLidUserForPn(pnUser, out lidUser))
                {
                    return false;
                }

                pairedJid = BuildLidForPnQuery(jid, lidUser);
                return !string.IsNullOrEmpty(pairedJid);
            }

            return false;
        }

        private async Task<IReadOnlyList<LidMapping>> ResolveLidsForPnsAsync(IReadOnlyList<string> pns)
        {
            var resolved = new Dictionary<string, LidMapping>(StringComparer.Ordinal);
            var pending = new List<string>();

            foreach (var pn in pns)
            {
                if (!JidUtils.IsPnUser(pn) && !JidUtils.IsHostedPnUser(pn))
                {
                    continue;
                }

                var pnUser = JidUtils.GetUser(pn);
                if (pnUser == null)
                {
                    continue;
                }

                var cached = _cache.Get(PnKey(pnUser));
                if (!string.IsNullOrEmpty(cached))
                {
                    AddLidPair(resolved, pn, cached);
                    continue;
                }

                pending.Add(pn);
            }

            if (pending.Count > 0)
            {
                var pendingUsers = pending.Select(JidUtils.GetUser).Where(u => u != null).Distinct(StringComparer.Ordinal);
                var stored = await _storage.GetAsync(pendingUsers.ToList()).ConfigureAwait(false);
                if (stored != null)
                {
                    foreach (var entry in stored)
                    {
                        if (!string.IsNullOrEmpty(entry.Value))
                        {
                            CachePair(entry.Key, entry.Value);
                        }
                    }
                }

                // Grouped by the JID usync will be asked about, keeping the devices each caller
                // asked for so the answer can be expanded back per device.
                var usyncFetch = new Dictionary<string, List<int>>(StringComparer.Ordinal);

                foreach (var pn in pending)
                {
                    var pnUser = JidUtils.GetUser(pn);
                    var cached = pnUser != null ? _cache.Get(PnKey(pnUser)) : null;
                    if (!string.IsNullOrEmpty(cached))
                    {
                        AddLidPair(resolved, pn, cached);
                        continue;
                    }

                    // Hosted numbers are asked about under their plain form; the server has no
                    // separate record for the hosted endpoint.
                    var lookupJid = pnUser + "@" + JidUtils.ServerWhatsApp;

                    List<int> devices;
                    if (!usyncFetch.TryGetValue(lookupJid, out devices))
                    {
                        devices = new List<int>();
                        usyncFetch[lookupJid] = devices;
                    }

                    devices.Add(JidUtils.GetDevice(pn));
                }

                if (usyncFetch.Count > 0)
                {
                    await ResolveThroughUsyncAsync(usyncFetch, resolved).ConfigureAwait(false);
                }
            }

            return resolved.Count > 0 ? resolved.Values.ToList() : null;
        }

        private async Task ResolveThroughUsyncAsync(
            Dictionary<string, List<int>> usyncFetch,
            Dictionary<string, LidMapping> resolved)
        {
            var resolver = PnToLidResolver;
            if (resolver == null)
            {
                _log.Trace("[LidMapping] " + usyncFetch.Count + " number(s) unresolved and no usync resolver is wired");
                return;
            }

            IReadOnlyList<LidMapping> fetched;
            try
            {
                fetched = await resolver(usyncFetch.Keys.ToList()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[LidMapping] usync lookup failed", ex);
                return;
            }

            if (fetched == null || fetched.Count == 0)
            {
                _log.Warn("[LidMapping] usync returned no mapping for the pending numbers");
                return;
            }

            await StoreMappingsAsync(fetched).ConfigureAwait(false);

            foreach (var pair in fetched)
            {
                var pnUser = JidUtils.GetUser(pair.Pn);
                var lidUser = JidUtils.GetUser(pair.Lid);
                if (pnUser == null || lidUser == null)
                {
                    continue;
                }

                if (!usyncFetch.TryGetValue(pair.Pn, out var devices))
                {
                    continue;
                }

                foreach (var device in devices)
                {
                    var pnJid = JidUtils.BuildPnJid(pnUser, device);
                    resolved[pnJid] = new LidMapping(JidUtils.BuildLidJid(lidUser, device), pnJid);
                }
            }
        }

        private async Task<IReadOnlyList<LidMapping>> ResolvePnsForLidsAsync(IReadOnlyList<string> lids)
        {
            var resolved = new Dictionary<string, LidMapping>(StringComparer.Ordinal);
            var pending = new List<string>();

            foreach (var lid in lids)
            {
                if (!JidUtils.IsAnyLid(lid))
                {
                    continue;
                }

                var lidUser = JidUtils.GetUser(lid);
                if (lidUser == null)
                {
                    continue;
                }

                var cached = _cache.Get(LidKey(lidUser));
                if (!string.IsNullOrEmpty(cached))
                {
                    AddPnPair(resolved, lid, cached);
                    continue;
                }

                pending.Add(lid);
            }

            if (pending.Count > 0)
            {
                var reverseKeys = pending
                    .Select(JidUtils.GetUser)
                    .Where(u => u != null)
                    .Distinct(StringComparer.Ordinal)
                    .Select(u => u + ReverseKeySuffix)
                    .ToList();

                var stored = await _storage.GetAsync(reverseKeys).ConfigureAwait(false);

                foreach (var lid in pending)
                {
                    var lidUser = JidUtils.GetUser(lid);
                    if (lidUser == null)
                    {
                        continue;
                    }

                    var pnUser = _cache.Get(LidKey(lidUser));
                    if (string.IsNullOrEmpty(pnUser) && stored != null &&
                        stored.TryGetValue(lidUser + ReverseKeySuffix, out var storedPnUser) &&
                        !string.IsNullOrEmpty(storedPnUser))
                    {
                        pnUser = storedPnUser;
                        CachePair(pnUser, lidUser);
                    }

                    if (string.IsNullOrEmpty(pnUser))
                    {
                        _log.Trace("[LidMapping] No reverse mapping for LID user " + lidUser);
                        continue;
                    }

                    AddPnPair(resolved, lid, pnUser);
                }
            }

            return resolved.Count > 0 ? resolved.Values.ToList() : null;
        }

        /// <summary>
        /// Reads the LID currently stored for each phone-number user, consulting the cache first.
        /// </summary>
        private async Task<Dictionary<string, string>> ReadExistingLidUsersAsync(IEnumerable<string> pnUsers)
        {
            var existing = new Dictionary<string, string>(StringComparer.Ordinal);
            var misses = new List<string>();

            foreach (var pnUser in pnUsers.Distinct(StringComparer.Ordinal))
            {
                var cached = _cache.Get(PnKey(pnUser));
                if (!string.IsNullOrEmpty(cached))
                {
                    existing[pnUser] = cached;
                    continue;
                }

                string pendingLid;
                lock (_pendingGate)
                {
                    _pendingDisk.TryGetValue(pnUser, out pendingLid);
                }

                if (!string.IsNullOrEmpty(pendingLid))
                {
                    existing[pnUser] = pendingLid;
                }
                else
                {
                    misses.Add(pnUser);
                }
            }

            if (misses.Count == 0)
            {
                return existing;
            }

            var stored = await _storage.GetAsync(misses).ConfigureAwait(false);
            if (stored == null)
            {
                return existing;
            }

            foreach (var entry in stored)
            {
                if (string.IsNullOrEmpty(entry.Value))
                {
                    continue;
                }

                existing[entry.Key] = entry.Value;
                CachePair(entry.Key, entry.Value);
            }

            return existing;
        }

        /// <summary>Re-attaches the device the caller asked about to the mapped LID user.</summary>
        private void AddLidPair(IDictionary<string, LidMapping> resolved, string pn, string lidUser)
        {
            if (string.IsNullOrEmpty(lidUser))
            {
                _log.Warn("[LidMapping] Empty LID user stored for " + pn);
                return;
            }

            string lid = BuildLidForPnQuery(pn, lidUser);
            if (string.IsNullOrEmpty(lid))
            {
                return;
            }

            resolved[pn] = new LidMapping(lid, pn);
        }

        private static void AddPnPair(IDictionary<string, LidMapping> resolved, string lid, string pnUser)
        {
            string pn = BuildPnForLidQuery(lid, pnUser);
            if (string.IsNullOrEmpty(pn))
            {
                return;
            }

            resolved[lid] = new LidMapping(lid, pn);
        }

        private static string BuildLidForPnQuery(string pn, string lidUser)
        {
            if (string.IsNullOrEmpty(lidUser))
            {
                return null;
            }

            int device = JidUtils.GetDevice(pn);

            // A hosted phone number maps onto the hosted LID server regardless of device id.
            if (JidUtils.IsHostedPnUser(pn))
            {
                return lidUser + (device > 0 ? ":" + device : string.Empty) + "@" + JidUtils.ServerHostedLid;
            }

            return JidUtils.BuildLidJid(lidUser, device);
        }

        private static string BuildPnForLidQuery(string lid, string pnUser)
        {
            if (string.IsNullOrEmpty(pnUser))
            {
                return null;
            }

            int device = JidUtils.GetDevice(lid);
            if (JidUtils.IsHostedLidUser(lid))
            {
                return pnUser + (device > 0 ? ":" + device : string.Empty) + "@" + JidUtils.ServerHosted;
            }

            return JidUtils.BuildPnJid(pnUser, device);
        }

        /// <summary>
        /// Runs <paramref name="work"/> unless an identical request is already running, in which
        /// case both callers await the same task. Ten chats resolving at once cost one query.
        /// </summary>
        private Task<IReadOnlyList<LidMapping>> CoalesceAsync(
            Dictionary<string, Task<IReadOnlyList<LidMapping>>> inflight,
            IEnumerable<string> input,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> work)
        {
            if (input == null)
            {
                return Task.FromResult<IReadOnlyList<LidMapping>>(null);
            }

            var jids = input
                .Where(j => !string.IsNullOrEmpty(j))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(j => j, StringComparer.Ordinal)
                .ToList();

            if (jids.Count == 0)
            {
                return Task.FromResult<IReadOnlyList<LidMapping>>(null);
            }

            var key = string.Join(",", jids);

            lock (_inflightGate)
            {
                if (inflight.TryGetValue(key, out var running))
                {
                    return running;
                }

                // The slot is published before the work starts. Starting first and assigning
                // after looks equivalent and is not: when the work answers from cache it never
                // really awaits, so it finished - and ran the removal in its finally - before
                // the assignment below had put anything there. The removal hit nothing and the
                // completed task was then filed permanently, so every later request for the
                // same addresses got that one frozen answer back. A pairing corrected on the
                // wire, or aged past its three-day life, would never be looked up again.
                var completion = new TaskCompletionSource<IReadOnlyList<LidMapping>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                inflight[key] = completion.Task;
                _ = RunAndReleaseAsync(inflight, key, completion, work, jids);
                return completion.Task;
            }
        }

        private async Task RunAndReleaseAsync(
            Dictionary<string, Task<IReadOnlyList<LidMapping>>> inflight,
            string key,
            TaskCompletionSource<IReadOnlyList<LidMapping>> completion,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> work,
            IReadOnlyList<string> jids)
        {
            try
            {
                IReadOnlyList<LidMapping> result = await work(jids).ConfigureAwait(false);
                completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
            finally
            {
                lock (_inflightGate)
                {
                    inflight.Remove(key);
                }
            }
        }

        private void CachePair(string pnUser, string lidUser)
        {
            _cache.Set(PnKey(pnUser), lidUser);
            _cache.Set(LidKey(lidUser), pnUser);
        }

        private static string PnKey(string pnUser)
        {
            return "pn:" + pnUser;
        }

        private static string LidKey(string lidUser)
        {
            return "lid:" + lidUser;
        }
    }
}
