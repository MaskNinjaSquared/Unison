// =============================================================================
// JidAliasTable
//
// The LID/phone map, and the canonical-address rules that read it.
//
// Extracted from WhatsAppService, where the map was a property and the rules
// were four private methods around it. Keeping them apart was the problem: the
// rules are the only reason the map exists, and every caller that reached for
// the dictionary directly was one reimplementing a rule badly.
//
// The long-term home for LID/PN pairs is LidMappingStore, which is SQLite-backed
// and async. This table stays synchronous on purpose, because canonicalization
// sits on the incoming-message path and behind XAML bindings, where there is no
// thread to await on. Converging the two is a storage swap behind this seam.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Unison.Core.Helpers;

namespace Unison.Core.State
{
    public sealed class JidAliasTable : IDictionary<string, string>, IReadOnlyDictionary<string, string>
    {
        private readonly object _sync = new object();
        private readonly Dictionary<string, string> _inner = new Dictionary<string, string>();

        private Func<string> _selfId;
        private Func<string> _selfLid;

        /// <summary>
        /// Raised when the map changed, which is what lets caches keyed by canonical address
        /// know they went stale.
        /// </summary>
        /// <remarks>
        /// A plain dictionary with the callers bumping a counter would do the same, and did not:
        /// the map is written from twenty-odd places, and the one that is added next is the one
        /// that forgets. Here there is nowhere to forget it.
        /// </remarks>
        public event EventHandler Changed;

        /// <summary>
        /// Points the table at the logged-in account. Deliberately two functions rather than two
        /// strings: the account's own LID arrives after pairing and is written in place on the
        /// existing auth state, so a snapshot taken here would be a snapshot of null, and the
        /// self-poisoning guards below would quietly stop firing.
        /// </summary>
        public void BindSelf(Func<string> selfId, Func<string> selfLid)
        {
            _selfId = selfId;
            _selfLid = selfLid;
        }

        // ---------------------------------------------------------------------
        // Canonical address
        // ---------------------------------------------------------------------

        /// <summary>
        /// The address a contact should be filed under, given everything the session has learned.
        /// Returns the input normalized when nothing is known about it.
        /// </summary>
        public string GetCanonicalJid(string jid)
        {
            if (string.IsNullOrEmpty(jid)) return jid;
            string normalized = Normalize(jid);

            if (TryGetValue(normalized, out var alias))
            {
                string normalizedAlias = Normalize(alias);

                bool isBidirectionalSelfAlias =
                    IsSelfLinked(normalizedAlias) &&
                    TryGetValue(normalizedAlias, out var reverseAlias) &&
                    string.Equals(Normalize(reverseAlias), normalized, StringComparison.OrdinalIgnoreCase);

                // Guard: never canonicalize a non-self contact to our own JID.
                if (!IsSelfLinked(normalized) && IsSelfLinked(normalizedAlias) && !isBidirectionalSelfAlias)
                {
                    Debug.WriteLine($"[JidAliasTable] Ignoring alias that maps contact to self: {normalized} -> {normalizedAlias}");
                    return normalized;
                }

                // Some devices surface LID-like identifiers on @s.whatsapp.net (e.g. 931....1@s.whatsapp.net).
                // If both ends are @s.whatsapp.net, prefer the non-instance form as canonical.
                bool normalizedIsPn = normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                bool aliasIsPn = normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase);
                if (normalizedIsPn && aliasIsPn)
                {
                    bool normalizedIsLidLike = IsLidLike(normalized);
                    bool aliasIsLidLike = IsLidLike(normalizedAlias);
                    if (normalizedIsLidLike && !aliasIsLidLike) return normalizedAlias;
                    if (!normalizedIsLidLike && aliasIsLidLike) return normalized;
                }

                // Favor @s.whatsapp.net (PN) as the canonical JID if both are available
                if (normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLike(normalizedAlias)) return normalizedAlias;
                if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) && !IsLidLike(normalized)) return normalized;

                return normalizedAlias;
            }

            string lidLikeAlias = GetCanonicalForLidLikeSWhatsappJid(normalized);
            if (!string.IsNullOrWhiteSpace(lidLikeAlias))
            {
                return lidLikeAlias;
            }

            if (IsSelfLinked(normalized))
            {
                string selfJid = GetCanonicalSelfPnJid();
                if (!string.IsNullOrWhiteSpace(selfJid))
                {
                    return selfJid;
                }
            }

            return normalized;
        }

        /// <summary>
        /// Whether the JID is the logged-in account: directly, through an alias, or as a
        /// device-suffixed PN whose base LID we are known by.
        /// </summary>
        public bool IsSelfLinked(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid) || !HasSelf)
            {
                return false;
            }

            string normalized = Normalize(jid);
            if (IsSelfJid(normalized))
            {
                return true;
            }

            if (TryGetValue(normalized, out var alias) && IsSelfJid(alias))
            {
                return true;
            }

            if (normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                normalized.Split('@')[0].Contains("."))
            {
                string user = normalized.Split('@')[0];
                int dotIndex = user.IndexOf('.');
                if (dotIndex > 0)
                {
                    string baseLid = $"{user.Substring(0, dotIndex)}@lid";
                    if (TryGetValue(baseLid, out var baseAlias) && IsSelfJid(baseAlias))
                    {
                        return true;
                    }
                }
            }

            string candidateUser = GetBaseUserPart(normalized);
            if (string.IsNullOrWhiteSpace(candidateUser))
            {
                return false;
            }

            string meIdUser = GetBaseUserPart(Normalize(SelfId));
            string meLidUser = GetBaseUserPart(Normalize(SelfLid));

            return string.Equals(candidateUser, meIdUser, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(candidateUser, meLidUser, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The account itself, without consulting aliases.</summary>
        public bool IsSelfJid(string jid)
        {
            if (string.IsNullOrEmpty(jid) || !HasSelf) return false;

            string normalized = Normalize(jid);
            string meId = Normalize(SelfId);
            string meLid = Normalize(SelfLid);

            return normalized == meId || (!string.IsNullOrEmpty(meLid) && normalized == meLid);
        }

        /// <summary>
        /// A LID, or one of the LID-shaped identifiers some devices surface on @s.whatsapp.net,
        /// which carry an instance suffix after a dot.
        /// </summary>
        public bool IsLidLike(string jid)
        {
            string normalized = Normalize(jid);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return false;
            }

            if (normalized.EndsWith("@lid", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string user = normalized.Split('@')[0];
            return user.Contains(".");
        }

        /// <summary>The account's own phone JID, preferred over its LID wherever one is known.</summary>
        public string GetCanonicalSelfPnJid()
        {
            string meId = Normalize(SelfId);
            if (!string.IsNullOrWhiteSpace(meId) &&
                meId.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                !IsLidLike(meId))
            {
                return meId;
            }

            string meLid = Normalize(SelfLid);
            if (!string.IsNullOrWhiteSpace(meLid) &&
                TryGetValue(meLid, out var alias))
            {
                string normalizedAlias = Normalize(alias);
                if (!string.IsNullOrWhiteSpace(normalizedAlias) &&
                    normalizedAlias.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase) &&
                    !IsLidLike(normalizedAlias))
                {
                    return normalizedAlias;
                }
            }

            if (!string.IsNullOrWhiteSpace(meId))
            {
                return meId;
            }

            return string.IsNullOrWhiteSpace(meLid) ? null : meLid;
        }

        private string GetCanonicalForLidLikeSWhatsappJid(string normalized)
        {
            if (string.IsNullOrWhiteSpace(normalized) ||
                !normalized.EndsWith("@s.whatsapp.net", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string user = normalized.Split('@')[0];
            int dotIndex = user.IndexOf('.');
            if (dotIndex <= 0)
            {
                return null;
            }

            string baseLid = $"{user.Substring(0, dotIndex)}@lid";
            if (TryGetValue(baseLid, out var alias))
            {
                string canonical = Normalize(alias);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    bool isBidirectionalSelfAlias =
                        IsSelfLinked(canonical) &&
                        TryGetValue(canonical, out var reverseAlias) &&
                        string.Equals(Normalize(reverseAlias), baseLid, StringComparison.OrdinalIgnoreCase);

                    if (!IsSelfLinked(baseLid) && IsSelfLinked(canonical) && !isBidirectionalSelfAlias)
                    {
                        Debug.WriteLine($"[JidAliasTable] Ignoring dotted alias that maps contact to self: {normalized} -> {canonical}");
                        return null;
                    }

                    return GetCanonicalJid(canonical);
                }
            }

            if (IsSelfLinked(baseLid))
            {
                return GetCanonicalSelfPnJid();
            }

            return null;
        }

        private static string GetBaseUserPart(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            string trimmed = jid.Trim();
            int atIndex = trimmed.IndexOf('@');
            string user = atIndex > 0 ? trimmed.Substring(0, atIndex) : trimmed;

            int colonIndex = user.IndexOf(':');
            if (colonIndex > 0)
            {
                user = user.Substring(0, colonIndex);
            }

            int dotIndex = user.IndexOf('.');
            if (dotIndex > 0)
            {
                user = user.Substring(0, dotIndex);
            }

            return user;
        }

        private static string Normalize(string jid) => JidHelper.Normalize(jid);

        private string SelfId => _selfId == null ? null : _selfId();

        private string SelfLid => _selfLid == null ? null : _selfLid();

        private bool HasSelf =>
            !string.IsNullOrEmpty(SelfId) || !string.IsNullOrEmpty(SelfLid);

        // ---------------------------------------------------------------------
        // Dictionary surface
        // ---------------------------------------------------------------------

        /// <summary>Thread-safe copy for persist / socket handoff.</summary>
        public Dictionary<string, string> Snapshot()
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_inner, StringComparer.OrdinalIgnoreCase);
            }
        }

        public string this[string key]
        {
            get
            {
                lock (_sync)
                {
                    return _inner[key];
                }
            }
            set
            {
                bool notify = false;
                lock (_sync)
                {
                    string existing;
                    if (_inner.TryGetValue(key, out existing) &&
                        string.Equals(existing, value, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _inner[key] = value;
                    notify = true;
                }

                if (notify)
                {
                    RaiseChanged();
                }
            }
        }

        public int Count
        {
            get { lock (_sync) { return _inner.Count; } }
        }

        public bool IsReadOnly => false;

        public ICollection<string> Keys
        {
            get { lock (_sync) { return _inner.Keys.ToList(); } }
        }

        public ICollection<string> Values
        {
            get { lock (_sync) { return _inner.Values.ToList(); } }
        }

        IEnumerable<string> IReadOnlyDictionary<string, string>.Keys => Keys;
        IEnumerable<string> IReadOnlyDictionary<string, string>.Values => Values;

        public bool ContainsKey(string key)
        {
            lock (_sync)
            {
                return _inner.ContainsKey(key);
            }
        }

        public bool TryGetValue(string key, out string value)
        {
            lock (_sync)
            {
                return _inner.TryGetValue(key, out value);
            }
        }

        public bool Contains(KeyValuePair<string, string> item)
        {
            lock (_sync)
            {
                return ((ICollection<KeyValuePair<string, string>>)_inner).Contains(item);
            }
        }

        public void CopyTo(KeyValuePair<string, string>[] array, int arrayIndex)
        {
            lock (_sync)
            {
                ((ICollection<KeyValuePair<string, string>>)_inner).CopyTo(array, arrayIndex);
            }
        }

        public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        {
            return Snapshot().GetEnumerator();
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
        {
            return GetEnumerator();
        }

        public void Add(string key, string value)
        {
            lock (_sync)
            {
                _inner.Add(key, value);
            }

            RaiseChanged();
        }

        public void Add(KeyValuePair<string, string> item) => Add(item.Key, item.Value);

        public bool Remove(string key)
        {
            bool removed;
            lock (_sync)
            {
                removed = _inner.Remove(key);
            }

            if (!removed)
            {
                return false;
            }

            RaiseChanged();
            return true;
        }

        public bool Remove(KeyValuePair<string, string> item)
        {
            bool removed;
            lock (_sync)
            {
                removed = ((ICollection<KeyValuePair<string, string>>)_inner).Remove(item);
            }

            if (!removed)
            {
                return false;
            }

            RaiseChanged();
            return true;
        }

        public void Clear()
        {
            bool hadItems;
            lock (_sync)
            {
                hadItems = _inner.Count > 0;
                if (hadItems)
                {
                    _inner.Clear();
                }
            }

            if (hadItems)
            {
                RaiseChanged();
            }
        }

        private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
    }
}
