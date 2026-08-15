// =============================================================================
// GetUSyncDevicesUseCase
//
// Finds every device a message has to be encrypted for.
//
// It asks for the device list and the LID column together, which is the change
// rc14 makes and the current code misses: the same round trip that tells us
// which devices exist also tells us the account's LID, so mappings are learned
// as a side effect of sending rather than through a separate lookup that may
// never happen. Newly mapped LIDs get their sessions refreshed immediately, so
// the first message addressed that way is not the one that fails.
//
// Ports: rc14 getUSyncDevices in src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Signal;
using Unison.Socket.USync;
using Unison.Socket.UseCases.USync;
using Unison.Socket.Utils;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Messages
{
    public sealed class GetUSyncDevicesUseCase
    {
        private static readonly TimeSpan DeviceCacheLifetime = TimeSpan.FromMinutes(5);

        private readonly ExecuteUSyncQueryUseCase _usync;
        private readonly ISignalRepository _signal;
        private readonly AssertSessionsUseCase _assertSessions;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly ISocketLog _log;

        private readonly TtlCache<IReadOnlyList<DeviceJid>> _cache =
            new TtlCache<IReadOnlyList<DeviceJid>>(DeviceCacheLifetime, 512);

        public GetUSyncDevicesUseCase(
            ExecuteUSyncQueryUseCase usync,
            ISignalRepository signal,
            AssertSessionsUseCase assertSessions,
            Func<string> meId,
            Func<string> meLid,
            ISocketLog log = null)
        {
            if (usync == null)
            {
                throw new ArgumentNullException(nameof(usync));
            }

            _usync = usync;
            _signal = signal;
            _assertSessions = assertSessions;
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <param name="useCache">
        /// False forces a fresh lookup. Worth doing when a message has just failed, since a
        /// stale device list is one of the reasons it would.
        /// </param>
        /// <param name="ignoreZeroDevices">
        /// Excludes the account's primary device. Used for group fan-out, where the primary is
        /// reached through the group's sender key instead.
        /// </param>
        public async Task<IReadOnlyList<DeviceJid>> ExecuteAsync(
            IEnumerable<string> jids,
            bool useCache = true,
            bool ignoreZeroDevices = false)
        {
            var devices = new List<DeviceJid>();
            if (jids == null)
            {
                return devices;
            }

            var toFetch = new List<string>();

            foreach (var jid in jids.Where(j => !string.IsNullOrEmpty(j)).Distinct(StringComparer.Ordinal))
            {
                // A JID that already names a device is an answer, not a question.
                if (HasExplicitDevice(jid))
                {
                    devices.Add(new DeviceJid
                    {
                        User = JidUtils.GetUser(jid),
                        Device = JidUtils.GetDevice(jid),
                        Server = JidUtils.GetServer(jid),
                        Jid = jid
                    });

                    continue;
                }

                var normalized = JidUtils.NormalizedUser(jid);
                var user = JidUtils.GetUser(jid);

                IReadOnlyList<DeviceJid> cached;
                if (useCache && user != null && _cache.TryGet(user, out cached))
                {
                    devices.AddRange(cached);
                    continue;
                }

                toFetch.Add(string.IsNullOrEmpty(normalized) ? jid : normalized);
            }

            if (toFetch.Count == 0)
            {
                return devices;
            }

            var query = new USyncQuery()
                .WithContext("message")
                .WithDeviceProtocol()
                .WithLidProtocol();

            foreach (var jid in toFetch)
            {
                query.WithUser(new USyncUser().WithId(jid));
            }

            var result = await _usync.ExecuteAsync(query).ConfigureAwait(false);
            if (result == null)
            {
                return devices;
            }

            await LearnLidMappingsAsync(result).ConfigureAwait(false);

            // Users asked about by LID must be answered by LID, whatever the server echoed.
            var lidUsers = new HashSet<string>(
                toFetch.Where(JidUtils.IsAnyLid).Select(JidUtils.GetUser).Where(u => u != null),
                StringComparer.Ordinal);

            var extracted = DeviceJidExtractor.Extract(result, _meId(), _meLid(), ignoreZeroDevices);

            var byUser = new Dictionary<string, List<DeviceJid>>(StringComparer.Ordinal);
            foreach (var device in extracted)
            {
                if (lidUsers.Contains(device.User))
                {
                    device.Jid = WA.JidEncode(device.User, device.Server, device.Device);
                }

                List<DeviceJid> bucket;
                if (!byUser.TryGetValue(device.User, out bucket))
                {
                    bucket = new List<DeviceJid>();
                    byUser[device.User] = bucket;
                }

                bucket.Add(device);
                devices.Add(device);
            }

            foreach (var pair in byUser)
            {
                _cache.Set(pair.Key, pair.Value);
            }

            return devices;
        }

        /// <summary>Forgets cached devices for a user, e.g. after a devices notification.</summary>
        public void InvalidateUser(string jid)
        {
            var user = JidUtils.GetUser(jid);
            if (user != null)
            {
                _cache.Remove(user);
            }
        }

        public void InvalidateAll()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Stores the LID/PN pairs the reply disclosed and opens the sessions we are missing for
        /// them, so a peer that later addresses us by LID is not a peer we cannot answer.
        /// </summary>
        private async Task LearnLidMappingsAsync(USyncQueryResult result)
        {
            if (_signal == null || _signal.LidMapping == null)
            {
                return;
            }

            var mappings = new List<LidMapping>();
            foreach (var entry in result.List)
            {
                string lid;
                if (entry.TryGet("lid", out lid) && !string.IsNullOrEmpty(lid) && !string.IsNullOrEmpty(entry.Id))
                {
                    mappings.Add(new LidMapping(lid, entry.Id));
                }
            }

            if (mappings.Count == 0)
            {
                return;
            }

            await _signal.LidMapping.StoreMappingsAsync(mappings).ConfigureAwait(false);

            if (_assertSessions == null)
            {
                return;
            }

            try
            {
                // Deliberately not forced: a forced fetch replaces whatever session already
                // exists under that LID, and the one it would replace is the one the peer's
                // incoming messages are being decrypted with.
                await _assertSessions.ExecuteAsync(mappings.Select(m => m.Lid)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Warn("[Devices] Could not refresh sessions for " + mappings.Count + " newly mapped LID(s)", ex);
            }
        }

        private static bool HasExplicitDevice(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return false;
            }

            var at = jid.IndexOf('@');
            var colon = jid.IndexOf(':');
            return colon > 0 && (at < 0 || colon < at);
        }
    }
}
