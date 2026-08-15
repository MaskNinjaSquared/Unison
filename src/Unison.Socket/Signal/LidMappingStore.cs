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

        private readonly ILidMappingStorage _storage;
        private readonly ISocketLog _log;
        private readonly TtlCache<string> _cache = new TtlCache<string>(CacheLifetime);

        private readonly object _inflightGate = new object();

        private readonly Dictionary<string, Task<IReadOnlyList<LidMapping>>> _inflightLidLookups =
            new Dictionary<string, Task<IReadOnlyList<LidMapping>>>(StringComparer.Ordinal);

        private readonly Dictionary<string, Task<IReadOnlyList<LidMapping>>> _inflightPnLookups =
            new Dictionary<string, Task<IReadOnlyList<LidMapping>>>(StringComparer.Ordinal);

        public LidMappingStore(ILidMappingStorage storage, ISocketLog log = null)
        {
            if (storage == null)
            {
                throw new ArgumentNullException(nameof(storage));
            }

            _storage = storage;
            _log = log ?? NullSocketLog.Instance;
        }

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

                var straight = JidUtils.IsLidUser(lid) && JidUtils.IsPnUser(pn);
                var swapped = JidUtils.IsPnUser(lid) && JidUtils.IsLidUser(pn);
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

            var batch = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var pair in toWrite)
            {
                batch[pair.Key] = pair.Value;
                batch[pair.Value + ReverseKeySuffix] = pair.Key;
            }

            await _storage.SetAsync(batch).ConfigureAwait(false);

            // Cache only after the write succeeded, so a failed write is retried instead of
            // being masked by a memory hit.
            foreach (var pair in toWrite)
            {
                CachePair(pair.Key, pair.Value);
            }

            _log.Trace("[LidMapping] Stored " + toWrite.Count + " mapping(s)");
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

            var device = JidUtils.GetDevice(pn);

            // A hosted phone number maps onto the hosted LID server regardless of device id.
            var lid = JidUtils.IsHostedPnUser(pn)
                ? lidUser + (device > 0 ? ":" + device : string.Empty) + "@" + JidUtils.ServerHostedLid
                : JidUtils.BuildLidJid(lidUser, device);

            resolved[pn] = new LidMapping(lid, pn);
        }

        private static void AddPnPair(IDictionary<string, LidMapping> resolved, string lid, string pnUser)
        {
            var device = JidUtils.GetDevice(lid);
            var pn = JidUtils.IsHostedLidUser(lid)
                ? pnUser + (device > 0 ? ":" + device : string.Empty) + "@" + JidUtils.ServerHosted
                : JidUtils.BuildPnJid(pnUser, device);

            resolved[lid] = new LidMapping(lid, pn);
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

                var task = RunAndReleaseAsync(inflight, key, work, jids);
                inflight[key] = task;
                return task;
            }
        }

        private async Task<IReadOnlyList<LidMapping>> RunAndReleaseAsync(
            Dictionary<string, Task<IReadOnlyList<LidMapping>>> inflight,
            string key,
            Func<IReadOnlyList<string>, Task<IReadOnlyList<LidMapping>>> work,
            IReadOnlyList<string> jids)
        {
            try
            {
                return await work(jids).ConfigureAwait(false);
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
