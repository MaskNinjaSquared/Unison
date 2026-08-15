// =============================================================================
// GroupMetadataProvider
//
// One group's metadata, fetched once and remembered for a while.
//
// The send path needs a group's participant list before it can hand out the
// sender key, and it needs it on every single message. Fetching it each time
// would put a round trip in front of every group send, so this sits in between
// with a short-lived cache and drops an entry as soon as the server says the
// group changed.
//
// Failing to resolve it is deliberately fatal to the send: a group message is
// encrypted once to a sender key, and the participants who never received that
// key cannot read it. Sending anyway produces a message that looks delivered and
// is unreadable, which is far worse than an error the caller can retry.
//
// Ports: rc14 cachedGroupMetadata and groupMetadata, as used by relayMessage in
// src/Socket/messages-send.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Models;
using Unison.Socket.Signal;
using Unison.Socket.UseCases.Groups;
using Unison.Socket.Utils;

namespace Unison.Socket.Groups
{
    public sealed class GroupMetadataProvider
    {
        /// <summary>
        /// Long enough that a burst of messages to the same group costs one query, short enough
        /// that a membership change we somehow missed a notification for still heals by itself.
        /// </summary>
        private static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(5);

        private readonly FetchGroupMetadataUseCase _fetch;
        private readonly TtlCache<GroupMetadata> _cache;
        private readonly ISocketLog _log;

        public GroupMetadataProvider(
            FetchGroupMetadataUseCase fetch,
            TimeSpan? lifetime = null,
            ISocketLog log = null)
        {
            if (fetch == null)
            {
                throw new ArgumentNullException(nameof(fetch));
            }

            _fetch = fetch;
            _cache = new TtlCache<GroupMetadata>(lifetime ?? DefaultLifetime, 256, false);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Called with the LID/PN pairs a fetch disclosed. A group of forty people discloses forty
        /// mappings in one round trip, which is the cheapest source of them there is.
        /// </summary>
        public Func<IReadOnlyList<LidMapping>, Task> MappingsDiscovered { get; set; }

        /// <exception cref="InvalidOperationException">The server refused, or answered with nothing.</exception>
        public async Task<GroupMetadata> GetAsync(string groupJid)
        {
            if (string.IsNullOrEmpty(groupJid))
            {
                throw new ArgumentException("groupJid is required", nameof(groupJid));
            }

            GroupMetadata cached;
            if (_cache.TryGet(groupJid, out cached))
            {
                return cached;
            }

            var result = await _fetch.ExecuteAsync(groupJid).ConfigureAwait(false);
            if (!result.HasMetadata)
            {
                throw new InvalidOperationException(
                    "Could not read the metadata of " + groupJid + " (" + (result.FailureReason ?? "unknown") + ")");
            }

            _cache.Set(groupJid, result.Metadata);

            var discovered = MappingsDiscovered;
            if (discovered != null && result.Mappings != null && result.Mappings.Count > 0)
            {
                await discovered(result.Mappings).ConfigureAwait(false);
            }

            _log.Debug(
                "[Groups] Read " + groupJid + ": " + result.Metadata.Participants.Count +
                " participant(s), addressed by " + result.Metadata.AddressingMode);

            return result.Metadata;
        }

        /// <summary>
        /// Files metadata that arrived without being asked for. A bulk fetch already carries
        /// everything a per-group query would return, so putting it here spares the send path a
        /// round trip per group the first time it writes to each one.
        /// </summary>
        public void Set(GroupMetadata metadata)
        {
            if (metadata != null && !string.IsNullOrEmpty(metadata.Id))
            {
                _cache.Set(metadata.Id, metadata);
            }
        }

        /// <summary>Drops a group so the next read goes back to the server.</summary>
        public void Invalidate(string groupJid)
        {
            if (!string.IsNullOrEmpty(groupJid) && _cache.Remove(groupJid))
            {
                _log.Debug("[Groups] Forgot the cached metadata of " + groupJid);
            }
        }

        public void Clear()
        {
            _cache.Clear();
        }
    }
}
