// =============================================================================
// GroupRosterIndex
//
// Memory-only roster per group, fed by everything that names a participant:
// message authors, quotes, w:gp2 add/remove/promote/demote, metadata replies,
// disk restores. Thread-safe and UI-free; the row (ChatItem.GroupMembers) is a
// projection the demand orchestrator flushes from TakePending.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class GroupRosterIndex
    {
        private const string LidServer = "@lid";
        private const string PhoneServer = "@s.whatsapp.net";
        private const string GroupServer = "@g.us";

        private readonly Func<string, string> _canonicalize;
        private readonly object _gate = new object();
        private readonly Dictionary<string, GroupState> _groups =
            new Dictionary<string, GroupState>(StringComparer.OrdinalIgnoreCase);

        /// <param name="canonicalize">
        /// LID / phone alias resolution. Must not promote or allocate on the UI thread's behalf
        /// (pass <c>JidAliasTable.PeekCanonicalJid</c>).
        /// </param>
        public GroupRosterIndex(Func<string, string> canonicalize)
        {
            _canonicalize = canonicalize ?? (jid => jid);
        }

        public GroupRosterCompleteness GetCompleteness(string groupJid)
        {
            lock (_gate)
            {
                GroupState state = Find(groupJid);
                return state?.Completeness ?? GroupRosterCompleteness.None;
            }
        }

        public void SetCompleteness(string groupJid, GroupRosterCompleteness completeness)
        {
            lock (_gate)
            {
                GroupState state = Ensure(groupJid);
                if (state != null)
                {
                    state.Completeness = completeness;
                }
            }
        }

        public GroupRosterRecord TryGet(string groupJid, string memberJid)
        {
            lock (_gate)
            {
                GroupState state = Find(groupJid);
                Entry entry = state == null ? null : FindEntry(state, KeysFor(memberJid));
                return entry?.ToRecord();
            }
        }

        public IReadOnlyList<GroupRosterRecord> Snapshot(string groupJid)
        {
            lock (_gate)
            {
                GroupState state = Find(groupJid);
                var records = new List<GroupRosterRecord>(state?.Entries.Count ?? 0);
                if (state != null)
                {
                    foreach (Entry entry in state.Entries)
                    {
                        records.Add(entry.ToRecord());
                    }
                }

                return records;
            }
        }

        /// <summary>Records one observation. True when the index changed (a flush is due).</summary>
        public bool Note(string groupJid, GroupMemberHint hint)
        {
            lock (_gate)
            {
                GroupState state = Ensure(groupJid);
                if (state == null)
                {
                    return false;
                }

                bool changed = Apply(state, hint, markPending: true);
                if (changed && state.Completeness == GroupRosterCompleteness.None)
                {
                    state.Completeness = GroupRosterCompleteness.Partial;
                }

                return changed;
            }
        }

        /// <summary>
        /// Loads members the row already shows (catalog / restore). Nothing is queued for a flush.
        /// </summary>
        public void Seed(string groupJid, IEnumerable<GroupMemberHint> hints, GroupRosterCompleteness completeness)
        {
            lock (_gate)
            {
                GroupState state = Ensure(groupJid);
                if (state == null)
                {
                    return;
                }

                if (hints != null)
                {
                    foreach (GroupMemberHint hint in hints)
                    {
                        Apply(state, hint, markPending: false);
                    }
                }

                if (completeness > state.Completeness)
                {
                    state.Completeness = completeness;
                }
                else if (state.Completeness == GroupRosterCompleteness.None && state.Entries.Count > 0)
                {
                    state.Completeness = GroupRosterCompleteness.Partial;
                }
            }
        }

        /// <summary>A leave / remove at <paramref name="atUtc"/>. Older sightings no longer re-add the member.</summary>
        public bool Remove(string groupJid, string memberJid, DateTime atUtc)
        {
            lock (_gate)
            {
                GroupState state = Ensure(groupJid);
                if (state == null)
                {
                    return false;
                }

                List<string> keys = KeysFor(memberJid);
                if (keys.Count == 0)
                {
                    return false;
                }

                Entry entry = FindEntry(state, keys);
                if (entry != null)
                {
                    keys.AddRange(entry.Keys);
                    Drop(state, entry, markPending: true);
                }

                foreach (string key in keys)
                {
                    DateTime prior;
                    if (!state.Tombstones.TryGetValue(key, out prior) || prior < atUtc)
                    {
                        state.Tombstones[key] = atUtc;
                    }
                }

                return entry != null;
            }
        }

        /// <summary>
        /// Folds a metadata reply in. Full when it listed <paramref name="expectedCount"/> members
        /// (or the count is unknown); a reply cut short stays Partial. Events newer than
        /// <paramref name="snapshotUtc"/> win: a later join stays, a later leave is not undone,
        /// a later promote keeps its role.
        /// </summary>
        public GroupRosterCompleteness MergeAll(
            string groupJid,
            IReadOnlyList<GroupMemberHint> members,
            int expectedCount,
            DateTime snapshotUtc,
            bool markPending = true)
        {
            lock (_gate)
            {
                GroupState state = Ensure(groupJid);
                if (state == null)
                {
                    return GroupRosterCompleteness.None;
                }

                var listed = new HashSet<Entry>();
                int count = 0;
                if (members != null)
                {
                    foreach (GroupMemberHint hint in members)
                    {
                        if (hint == null)
                        {
                            continue;
                        }

                        count++;
                        Apply(state, hint, markPending);
                        Entry entry = FindEntry(state, HintKeys(hint));
                        if (entry != null)
                        {
                            listed.Add(entry);
                        }
                    }
                }

                bool full = count > 0 && (expectedCount <= 0 || count >= expectedCount);
                if (full)
                {
                    var stale = new List<Entry>();
                    foreach (Entry entry in state.Entries)
                    {
                        if (!listed.Contains(entry) && entry.AddedAtUtc <= snapshotUtc)
                        {
                            stale.Add(entry);
                        }
                    }

                    foreach (Entry entry in stale)
                    {
                        Drop(state, entry, markPending);
                    }
                }

                if (count > 0 || state.Entries.Count > 0)
                {
                    state.Completeness = full ? GroupRosterCompleteness.Full : GroupRosterCompleteness.Partial;
                }

                if (full && snapshotUtc > state.FullSnapshotUtc)
                {
                    state.FullSnapshotUtc = snapshotUtc;
                }

                return state.Completeness;
            }
        }

        public bool HasPending(string groupJid)
        {
            lock (_gate)
            {
                GroupState state = Find(groupJid);
                return state != null && (state.PendingUpserts.Count > 0 || state.PendingRemovals.Count > 0);
            }
        }

        public GroupRosterDelta TakePending(string groupJid)
        {
            lock (_gate)
            {
                GroupState state = Find(groupJid);
                if (state == null)
                {
                    return new GroupRosterDelta(Group(groupJid), null, null, GroupRosterCompleteness.None);
                }

                var upserted = new List<GroupRosterRecord>(state.PendingUpserts.Count);
                foreach (Entry entry in state.PendingUpserts)
                {
                    upserted.Add(entry.ToRecord());
                }

                var removed = new List<GroupRosterRecord>(state.PendingRemovals);
                state.PendingUpserts.Clear();
                state.PendingRemovals.Clear();
                return new GroupRosterDelta(state.GroupJid, upserted, removed, state.Completeness);
            }
        }

        /// <summary>Logout / account switch.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _groups.Clear();
            }
        }

        private bool Apply(GroupState state, GroupMemberHint hint, bool markPending)
        {
            if (hint == null)
            {
                return false;
            }

            List<string> keys = HintKeys(hint);
            if (keys.Count == 0)
            {
                return false;
            }

            foreach (string key in keys)
            {
                DateTime removedAt;
                if (state.Tombstones.TryGetValue(key, out removedAt) && removedAt >= hint.ObservedAtUtc)
                {
                    return false;
                }
            }

            foreach (string key in keys)
            {
                state.Tombstones.Remove(key);
            }

            Entry entry = FindEntry(state, keys);
            bool changed = false;
            if (entry == null &&
                state.Completeness == GroupRosterCompleteness.Full &&
                hint.Source != GroupMemberHintSource.Metadata &&
                hint.ObservedAtUtc <= state.FullSnapshotUtc)
            {
                // The server listed everyone after this sighting and this person was not there.
                return false;
            }

            if (entry == null)
            {
                entry = new Entry { AddedAtUtc = hint.ObservedAtUtc };
                state.Entries.Add(entry);
                changed = true;
            }
            else
            {
                changed |= AbsorbDuplicates(state, entry, keys, markPending);
            }

            changed |= entry.Absorb(hint, Primary(hint), LidOf(hint), PhoneOf(hint));
            Index(state, entry);

            if (changed && markPending)
            {
                state.PendingUpserts.Add(entry);
            }

            return changed;
        }

        private bool AbsorbDuplicates(GroupState state, Entry keep, List<string> keys, bool markPending)
        {
            bool merged = false;
            foreach (string key in keys)
            {
                Entry other;
                if (!state.ByKey.TryGetValue(key, out other) || ReferenceEquals(other, keep))
                {
                    continue;
                }

                keep.TakeFieldsFrom(other);
                Drop(state, other, markPending);
                merged = true;
            }

            return merged;
        }

        private void Drop(GroupState state, Entry entry, bool markPending)
        {
            state.Entries.Remove(entry);
            state.PendingUpserts.Remove(entry);
            foreach (string key in entry.Keys)
            {
                Entry owner;
                if (state.ByKey.TryGetValue(key, out owner) && ReferenceEquals(owner, entry))
                {
                    state.ByKey.Remove(key);
                }
            }

            if (markPending)
            {
                state.PendingRemovals.Add(entry.ToRecord());
            }
        }

        private void Index(GroupState state, Entry entry)
        {
            foreach (string key in KeysFor(entry.Jid, entry.Lid, entry.PhoneNumber))
            {
                entry.Keys.Add(key);
                state.ByKey[key] = entry;
            }
        }

        private static Entry FindEntry(GroupState state, List<string> keys)
        {
            foreach (string key in keys)
            {
                Entry entry;
                if (state.ByKey.TryGetValue(key, out entry))
                {
                    return entry;
                }
            }

            return null;
        }

        private GroupState Find(string groupJid)
        {
            string key = Group(groupJid);
            if (key == null)
            {
                return null;
            }

            GroupState state;
            return _groups.TryGetValue(key, out state) ? state : null;
        }

        private GroupState Ensure(string groupJid)
        {
            string key = Group(groupJid);
            if (key == null)
            {
                return null;
            }

            GroupState state;
            if (!_groups.TryGetValue(key, out state))
            {
                state = new GroupState(key);
                _groups[key] = state;
            }

            return state;
        }

        private static string Group(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return null;
            }

            string normalized = JidHelper.Normalize(groupJid);
            return normalized.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase) ? normalized : null;
        }

        private List<string> HintKeys(GroupMemberHint hint)
        {
            return KeysFor(hint.Jid, hint.AltJid, hint.Lid, PhoneJid(hint.PhoneNumber));
        }

        private List<string> KeysFor(params string[] addresses)
        {
            var keys = new List<string>();
            foreach (string raw in addresses)
            {
                string normalized = Person(raw);
                if (normalized == null)
                {
                    continue;
                }

                AddKey(keys, normalized);
                string canonical = Person(_canonicalize(normalized));
                if (canonical != null)
                {
                    AddKey(keys, canonical);
                }
            }

            return keys;
        }

        private static void AddKey(List<string> keys, string key)
        {
            for (int i = 0; i < keys.Count; i++)
            {
                if (string.Equals(keys[i], key, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            keys.Add(key);
        }

        private string Primary(GroupMemberHint hint)
        {
            string raw = Person(hint.Jid) ?? Person(hint.AltJid) ?? Person(hint.Lid) ?? Person(PhoneJid(hint.PhoneNumber));
            if (raw == null)
            {
                return null;
            }

            return Person(_canonicalize(raw)) ?? raw;
        }

        private static string LidOf(GroupMemberHint hint)
        {
            return FirstOnServer(LidServer, hint.Lid, hint.Jid, hint.AltJid);
        }

        private static string PhoneOf(GroupMemberHint hint)
        {
            if (!string.IsNullOrWhiteSpace(hint.PhoneNumber))
            {
                return hint.PhoneNumber.Trim();
            }

            return FirstOnServer(PhoneServer, hint.Jid, hint.AltJid);
        }

        private static string FirstOnServer(string server, params string[] addresses)
        {
            foreach (string raw in addresses)
            {
                string normalized = Person(raw);
                if (normalized != null && normalized.EndsWith(server, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized;
                }
            }

            return null;
        }

        private static string PhoneJid(string phone)
        {
            if (string.IsNullOrWhiteSpace(phone))
            {
                return null;
            }

            if (phone.IndexOf('@') >= 0)
            {
                return phone;
            }

            string digits = PhoneNumberHelper.NormalizePhoneDigits(phone);
            return string.IsNullOrEmpty(digits) ? null : digits + PhoneServer;
        }

        private static string Person(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string normalized = JidHelper.Normalize(raw);
            if (string.IsNullOrWhiteSpace(normalized) ||
                normalized.IndexOf('@') <= 0 ||
                normalized.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return normalized;
        }

        private sealed class GroupState
        {
            public GroupState(string groupJid)
            {
                GroupJid = groupJid;
            }

            public string GroupJid { get; }

            public GroupRosterCompleteness Completeness;

            public DateTime FullSnapshotUtc = DateTime.MinValue;

            public readonly List<Entry> Entries = new List<Entry>();

            public readonly Dictionary<string, Entry> ByKey =
                new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);

            public readonly Dictionary<string, DateTime> Tombstones =
                new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            public readonly HashSet<Entry> PendingUpserts = new HashSet<Entry>();

            public readonly List<GroupRosterRecord> PendingRemovals = new List<GroupRosterRecord>();
        }

        private sealed class Entry
        {
            public string Jid;
            public string Lid;
            public string PhoneNumber;
            public string DisplayName;
            public GroupParticipantRole Role;
            public DateTime RoleAtUtc = DateTime.MinValue;
            public DateTime AddedAtUtc;
            public readonly HashSet<string> Keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public bool Absorb(GroupMemberHint hint, string primary, string lid, string phone)
            {
                bool changed = false;
                if (string.IsNullOrWhiteSpace(Jid) && primary != null)
                {
                    Jid = primary;
                    changed = true;
                }

                changed |= Fill(ref Lid, lid);
                changed |= Fill(ref PhoneNumber, phone);

                string name = string.IsNullOrWhiteSpace(hint.DisplayName) ? null : hint.DisplayName.Trim();
                if (name != null && !string.Equals(DisplayName, name, StringComparison.Ordinal))
                {
                    DisplayName = name;
                    changed = true;
                }

                if (hint.Role.HasValue && hint.ObservedAtUtc >= RoleAtUtc)
                {
                    RoleAtUtc = hint.ObservedAtUtc;
                    if (Role != hint.Role.Value)
                    {
                        Role = hint.Role.Value;
                        changed = true;
                    }
                }

                if (hint.ObservedAtUtc < AddedAtUtc)
                {
                    AddedAtUtc = hint.ObservedAtUtc;
                }

                return changed;
            }

            public void TakeFieldsFrom(Entry other)
            {
                Fill(ref Lid, other.Lid);
                Fill(ref PhoneNumber, other.PhoneNumber);
                if (string.IsNullOrWhiteSpace(DisplayName))
                {
                    DisplayName = other.DisplayName;
                }

                if (other.RoleAtUtc > RoleAtUtc)
                {
                    Role = other.Role;
                    RoleAtUtc = other.RoleAtUtc;
                }

                if (other.AddedAtUtc < AddedAtUtc)
                {
                    AddedAtUtc = other.AddedAtUtc;
                }
            }

            public GroupRosterRecord ToRecord() => new GroupRosterRecord(Jid, Lid, PhoneNumber, DisplayName, Role);

            private static bool Fill(ref string field, string value)
            {
                if (!string.IsNullOrWhiteSpace(field) || string.IsNullOrWhiteSpace(value))
                {
                    return false;
                }

                field = value;
                return true;
            }
        }
    }
}
