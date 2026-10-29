// =============================================================================
// GroupRosterDemand
//
// One roster rule for every device. Members arrive one by one from whatever
// names them (an incoming message, a quote, a w:gp2 add or promote, a visible
// author, a disk row) and land in GroupRosterIndex off the UI thread. A
// debounced flush per group puts the delta on ChatItem.GroupMembers at low
// priority, after the open-chat timeline gate, and writes only that delta to
// SQLite. The whole list is fetched only when a consumer needs everyone (the @
// menu, the Members pivot): one request per group, reused for a window.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class GroupRosterDemand : IDisposable
    {
        private const string GroupServer = "@g.us";

        /// <summary>Members put on the row per dispatcher hop.</summary>
        public const int FlushBatchSize = 16;

        private static readonly TimeSpan DefaultFlushDebounce = TimeSpan.FromMilliseconds(250);

        /// <summary>A full fetch that answered recently is not asked again for this long.</summary>
        private static readonly TimeSpan FullFetchReuseWindow = TimeSpan.FromMinutes(2);

        private readonly GroupRosterIndex _index;
        private readonly IDispatcher _ui;
        private readonly ILowPriorityDispatcher _lowUi;
        private readonly IJidResolver _jids;
        private readonly IChatStateStore _chats;
        private readonly ChatRowIndex _rows;
        private readonly IGroupNaming _naming;
        private readonly IGroupRosterStore _store;
        private readonly GroupRosterWriter _writer;
        private readonly ChatTimelineLoadPriorityGate _timelinePriority;
        private readonly IRuntimeDiagnostics _diagnostics;
        private readonly TimeSpan _flushDebounce;
        private readonly Func<DateTime> _utcNow;

        private readonly object _gate = new object();
        private readonly Dictionary<string, CoalescedDebounceScheduler> _flushers =
            new Dictionary<string, CoalescedDebounceScheduler>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Task> _fullInFlight =
            new Dictionary<string, Task>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, DateTime> _lastFullFetchUtc =
            new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _storeRestored =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Stored rows waiting for their flush, so avatar and lookup history carry onto the member
        /// the flush creates (the index keeps identity, name and role only).
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, GroupMember>> _storedTemplates =
            new Dictionary<string, Dictionary<string, GroupMember>>(StringComparer.OrdinalIgnoreCase);

        private Func<string, CancellationToken, Task<bool>> _fullFetch;
        private bool _disposed;

        public GroupRosterDemand(
            GroupRosterIndex index,
            IDispatcher ui,
            IJidResolver jids,
            IChatStateStore chats,
            ChatRowIndex rows,
            IGroupNaming naming,
            IGroupRosterStore store,
            GroupRosterWriter writer,
            ChatTimelineLoadPriorityGate timelinePriority = null,
            IRuntimeDiagnostics diagnostics = null,
            TimeSpan? flushDebounce = null,
            Func<DateTime> utcNow = null)
        {
            _index = index ?? throw new ArgumentNullException(nameof(index));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _lowUi = ui as ILowPriorityDispatcher;
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
            _chats = chats ?? throw new ArgumentNullException(nameof(chats));
            _rows = rows ?? throw new ArgumentNullException(nameof(rows));
            _naming = naming ?? throw new ArgumentNullException(nameof(naming));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _writer = writer ?? throw new ArgumentNullException(nameof(writer));
            _timelinePriority = timelinePriority;
            _diagnostics = diagnostics;
            _flushDebounce = flushDebounce ?? DefaultFlushDebounce;
            _utcNow = utcNow ?? (() => DateTime.UtcNow);
        }

        public GroupRosterIndex Index => _index;

        /// <summary>
        /// The w:g2 metadata query with the roster, answering whether a reply was applied. Set by
        /// the façade that owns the wire, so this class never names it.
        /// </summary>
        public void AttachFullFetch(Func<string, CancellationToken, Task<bool>> fullFetch)
        {
            _fullFetch = fullFetch;
        }

        /// <summary>
        /// Memory only. The live row member when the row has one, otherwise a detached copy of
        /// what the index knows. UI thread (reads the row).
        /// </summary>
        public GroupMember TryGetMember(string groupJid, string memberJid)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null || string.IsNullOrWhiteSpace(memberJid))
            {
                return null;
            }

            ChatItem chat = FindRow(group);
            GroupMember live = FindOnRow(chat, memberJid);
            if (live != null)
            {
                return live;
            }

            GroupRosterRecord record = _index.TryGet(group, memberJid);
            return record == null ? null : ToMember(record, avatarUrl: null);
        }

        /// <summary>An event named a participant. No network; a flush follows when it was news.</summary>
        public void NoteMember(string groupJid, GroupMemberHint hint)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null || hint == null || _jids.IsSelfLinked(hint.Jid))
            {
                return;
            }

            if (_index.Note(group, hint))
            {
                Trace("roster", "note", "chat=" + DiagnosticJidMask.Apply(group) + "; source=" + hint.Source);
                ScheduleFlush(group);
            }
        }

        /// <summary>A leave or remove at <paramref name="atUtc"/>.</summary>
        public void RemoveMember(string groupJid, string memberJid, DateTime atUtc)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null)
            {
                return;
            }

            if (_index.Remove(group, memberJid, atUtc))
            {
                Trace("roster", "note", "chat=" + DiagnosticJidMask.Apply(group) + "; source=Leave");
                ScheduleFlush(group);
            }
        }

        /// <summary>
        /// Makes sure each of <paramref name="memberJids"/> is known: memory, then the store by
        /// primary key, then a bare entry with the best name the caches have.
        /// </summary>
        public async Task EnsureMembersAsync(string groupJid, IReadOnlyList<string> memberJids)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null || memberJids == null || memberJids.Count == 0)
            {
                return;
            }

            bool changed = false;
            for (int i = 0; i < memberJids.Count; i++)
            {
                string jid = memberJids[i];
                if (string.IsNullOrWhiteSpace(jid) || _jids.IsSelfLinked(jid) || _index.TryGet(group, jid) != null)
                {
                    continue;
                }

                GroupMember stored = await ReadStoredMemberAsync(group, jid).ConfigureAwait(false);
                GroupMemberHint hint = stored != null
                    ? GroupMemberHint.FromMember(stored, GroupMemberHintSource.Store, DateTime.MinValue)
                    : new GroupMemberHint
                    {
                        Jid = jid,
                        DisplayName = UsableName(_naming.ResolveMemberDisplayName(jid), jid),
                        Source = GroupMemberHintSource.Viewport,
                        ObservedAtUtc = DateTime.MinValue
                    };
                if (stored != null)
                {
                    KeepTemplate(group, stored);
                    if (string.IsNullOrWhiteSpace(hint.DisplayName))
                    {
                        hint.DisplayName = UsableName(_naming.ResolveMemberDisplayName(stored.Jid), stored.Jid);
                    }
                }

                changed |= _index.Note(group, hint);
            }

            if (changed)
            {
                // Keep open-chat / Normal UI free: merge members now without SQL, debounce the disk write.
                await FlushAsync(group, persist: false).ConfigureAwait(false);
                ScheduleFlush(group);
            }
        }

        /// <summary>
        /// Everyone in the group, for the @ menu / Members pivot. What the store knows lands first;
        /// then one metadata request per group, skipped when one answered within the reuse window.
        /// </summary>
        public Task EnsureFullRosterAsync(string groupJid, string reason, CancellationToken cancellationToken = default)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null)
            {
                return Task.CompletedTask;
            }

            lock (_gate)
            {
                Task running;
                if (_fullInFlight.TryGetValue(group, out running) && !running.IsCompleted)
                {
                    return running;
                }

                Task started = RunEnsureFullAsync(group, reason ?? "?", cancellationToken);
                _fullInFlight[group] = started;
                return started;
            }
        }

        /// <summary>
        /// Loads the persisted roster into the index and onto the row, once per group per session.
        /// Never hits the network.
        /// </summary>
        public async Task RestoreFromStoreAsync(string groupJid)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null || _index.GetCompleteness(group) == GroupRosterCompleteness.Full)
            {
                return;
            }

            lock (_gate)
            {
                if (!_storeRestored.Add(group))
                {
                    return;
                }
            }

            var watch = Stopwatch.StartNew();
            IReadOnlyList<GroupMember> stored;
            try
            {
                await _store.InitializeAsync().ConfigureAwait(false);
                stored = await _store.GetForGroupAsync(group).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterDemand] Store read failed: " + ex.Message);
                lock (_gate)
                {
                    _storeRestored.Remove(group);
                }

                return;
            }

            if (stored == null || stored.Count == 0)
            {
                return;
            }

            await SeedFromRowAsync(group).ConfigureAwait(false);
            bool changed = false;
            for (int i = 0; i < stored.Count; i++)
            {
                GroupMember row = stored[i];
                if (row == null || string.IsNullOrWhiteSpace(row.Jid))
                {
                    continue;
                }

                KeepTemplate(group, row);
                GroupMemberHint hint = GroupMemberHint.FromMember(row, GroupMemberHintSource.Store, DateTime.MinValue);
                if (string.IsNullOrWhiteSpace(hint.DisplayName))
                {
                    hint.DisplayName = UsableName(_naming.ResolveMemberDisplayName(row.Jid), row.Jid);
                }

                changed |= _index.Note(group, hint);
            }

            if (changed)
            {
                await FlushAsync(group, persist: false).ConfigureAwait(false);
            }

            Trace(
                "roster",
                "warm-store",
                "chat=" + DiagnosticJidMask.Apply(group) +
                "; stored=" + stored.Count +
                "; changed=" + changed +
                "; ms=" + watch.ElapsedMilliseconds);
        }

        /// <summary>Puts pending index changes on the row now (no debounce). Tests / ensure paths.</summary>
        public Task FlushNowAsync(string groupJid)
        {
            string group = CanonicalGroup(groupJid);
            return group == null ? Task.CompletedTask : FlushAsync(group, persist: true);
        }

        /// <summary>Session wipe.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                foreach (CoalescedDebounceScheduler flusher in _flushers.Values)
                {
                    flusher.Dispose();
                }

                _flushers.Clear();
                _lastFullFetchUtc.Clear();
                _storeRestored.Clear();
            }

            _index.Clear();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
            }

            Clear();
        }

        /// <summary>A metadata reply answered for <paramref name="groupJid"/> (any caller).</summary>
        public void NoteFullFetchAnswered(string groupJid)
        {
            string group = CanonicalGroup(groupJid);
            if (group == null)
            {
                return;
            }

            lock (_gate)
            {
                _lastFullFetchUtc[group] = _utcNow();
            }
        }

        private async Task RunEnsureFullAsync(string group, string reason, CancellationToken cancellationToken)
        {
            var elapsed = Stopwatch.StartNew();
            string outcome = "fetched";
            try
            {
                await Task.Yield();
                await RestoreFromStoreAsync(group).ConfigureAwait(false);

                if (_index.GetCompleteness(group) == GroupRosterCompleteness.Full)
                {
                    outcome = "already-full";
                    return;
                }

                if (AnsweredRecently(group))
                {
                    outcome = "reused";
                    return;
                }

                Func<string, CancellationToken, Task<bool>> fetch = _fullFetch;
                if (fetch == null)
                {
                    outcome = "no-fetch";
                    return;
                }

                if (_timelinePriority != null)
                {
                    await _timelinePriority.WaitWhileHeldAsync().ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (await fetch(group, cancellationToken).ConfigureAwait(false))
                {
                    NoteFullFetchAnswered(group);
                }
                else
                {
                    outcome = "not-applied";
                }
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
            }
            catch (Exception ex)
            {
                outcome = "failed";
                Debug.WriteLine("[GroupRosterDemand] EnsureFullRoster failed: " + ex.Message);
            }
            finally
            {
                elapsed.Stop();
                Trace(
                    "roster",
                    "ensure-full",
                    "chat=" + DiagnosticJidMask.Apply(group) +
                    "; reason=" + reason +
                    "; outcome=" + outcome +
                    "; ms=" + elapsed.ElapsedMilliseconds +
                    "; count=" + _index.Snapshot(group).Count +
                    "; completeness=" + _index.GetCompleteness(group));
            }
        }

        private bool AnsweredRecently(string group)
        {
            lock (_gate)
            {
                DateTime last;
                return _lastFullFetchUtc.TryGetValue(group, out last) && _utcNow() - last < FullFetchReuseWindow;
            }
        }

        private async Task<GroupMember> ReadStoredMemberAsync(string group, string jid)
        {
            try
            {
                await _store.InitializeAsync().ConfigureAwait(false);
                GroupMember stored = await _store.GetMemberAsync(group, jid).ConfigureAwait(false);
                if (stored != null)
                {
                    return stored;
                }

                string canonical = _jids.GetCanonicalJid(jid);
                if (!string.IsNullOrWhiteSpace(canonical) &&
                    !string.Equals(canonical, jid, StringComparison.OrdinalIgnoreCase))
                {
                    return await _store.GetMemberAsync(group, canonical).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterDemand] Store member read failed: " + ex.Message);
            }

            return null;
        }

        private void ScheduleFlush(string group)
        {
            CoalescedDebounceScheduler flusher;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                if (!_flushers.TryGetValue(group, out flusher))
                {
                    flusher = new CoalescedDebounceScheduler(_flushDebounce);
                    _flushers[group] = flusher;
                }
            }

            flusher.Schedule(() => _ = FlushAsync(group, persist: true));
        }

        /// <summary>
        /// The row may hold members from the catalog that the index never saw (cold start). Seed
        /// them first so a flush adds to them instead of replacing them.
        /// </summary>
        private Task SeedFromRowAsync(string group)
        {
            if (_index.GetCompleteness(group) != GroupRosterCompleteness.None)
            {
                return Task.CompletedTask;
            }

            return RunLowAsync(() =>
            {
                ChatItem chat = FindRow(group);
                if (chat?.GroupMembers == null || _index.GetCompleteness(group) != GroupRosterCompleteness.None)
                {
                    return;
                }

                var hints = new List<GroupMemberHint>(chat.GroupMembers.Count);
                foreach (GroupMember member in chat.GroupMembers)
                {
                    GroupMemberHint hint = GroupMemberHint.FromMember(member, GroupMemberHintSource.Store, DateTime.MinValue);
                    if (hint != null)
                    {
                        hints.Add(hint);
                    }
                }

                _index.Seed(group, hints, chat.RosterCompleteness);
            });
        }

        private async Task FlushAsync(string group, bool persist)
        {
            try
            {
                if (_timelinePriority != null && _timelinePriority.IsHeld)
                {
                    await _timelinePriority.WaitWhileHeldAsync().ConfigureAwait(false);
                }

                await SeedFromRowAsync(group).ConfigureAwait(false);
                GroupRosterDelta delta = _index.TakePending(group);
                if (delta.IsEmpty)
                {
                    return;
                }

                var elapsed = Stopwatch.StartNew();
                Dictionary<string, GroupMember> templates = TakeTemplates(group);
                var changedJids = new List<string>(delta.Upserted.Count + delta.Removed.Count);
                var added = new List<GroupMember>();
                var touched = new List<GroupMember>();
                bool fieldsChanged = false;
                ChatItem chat = null;

                for (int start = 0; start < delta.Upserted.Count; start += FlushBatchSize)
                {
                    int from = start;
                    await RunLowAsync(() =>
                    {
                        chat = chat ?? FindRow(group);
                        if (chat == null)
                        {
                            return;
                        }

                        int end = Math.Min(from + FlushBatchSize, delta.Upserted.Count);
                        for (int i = from; i < end; i++)
                        {
                            GroupRosterRecord record = delta.Upserted[i];
                            GroupMember live = FindOnRow(chat, record.Jid) ??
                                               FindOnRow(chat, record.Lid) ??
                                               FindOnRow(chat, record.PhoneNumber) ??
                                               FindAmong(added, record);
                            if (live != null)
                            {
                                fieldsChanged |= MergeOnto(live, record);
                                touched.Add(live);
                            }
                            else
                            {
                                live = FromTemplate(templates, record) ?? ToMember(
                                    record,
                                    ChatRowAvatarLookup.Find(_rows, record.Jid, record.PhoneNumber, record.Lid));
                                added.Add(live);
                                touched.Add(live);
                            }

                            changedJids.Add(record.Jid);
                        }
                    }).ConfigureAwait(false);

                    if (chat == null)
                    {
                        return;
                    }
                }

                var removedJids = new List<string>(delta.Removed.Count);
                var removedKeys = new List<string>();
                await RunLowAsync(() =>
                {
                    chat = chat ?? FindRow(group);
                    if (chat == null)
                    {
                        return;
                    }

                    var gone = new HashSet<GroupMember>();
                    for (int i = 0; i < delta.Removed.Count; i++)
                    {
                        GroupRosterRecord record = delta.Removed[i];
                        GroupMember live = FindOnRow(chat, record.Jid) ??
                                           FindOnRow(chat, record.Lid) ??
                                           FindOnRow(chat, record.PhoneNumber);
                        if (live != null && !touched.Contains(live))
                        {
                            gone.Add(live);
                            removedKeys.AddRange(GroupRosterRules.MembershipPersonKeys(live, _jids));
                        }

                        if (!string.IsNullOrWhiteSpace(record.Jid))
                        {
                            removedJids.Add(record.Jid);
                            changedJids.Add(record.Jid);
                        }
                    }

                    if (added.Count > 0 || gone.Count > 0)
                    {
                        var next = new List<GroupMember>((chat.GroupMembers?.Count ?? 0) + added.Count);
                        if (chat.GroupMembers != null)
                        {
                            foreach (GroupMember member in chat.GroupMembers)
                            {
                                if (member != null && !gone.Contains(member))
                                {
                                    next.Add(member);
                                }
                            }
                        }

                        next.AddRange(added);
                        chat.GroupMembers = next;
                    }
                    else if (fieldsChanged)
                    {
                        chat.RefreshMentionLookupFromRoster();
                    }

                    if (!chat.IsGroup)
                    {
                        chat.IsGroup = true;
                    }

                    chat.RosterCompleteness = delta.Completeness;
                    if (chat.HasGroupMembers && chat.GroupMemberCount < chat.GroupMembers.Count)
                    {
                        chat.GroupMemberCount = chat.GroupMembers.Count;
                    }

                    _chats.NotifyGroupRosterChanged(chat.JID, changedJids);
                }).ConfigureAwait(false);

                elapsed.Stop();
                Trace(
                    "roster",
                    "flush",
                    "chat=" + DiagnosticJidMask.Apply(group) +
                    "; n=" + (delta.Upserted.Count + delta.Removed.Count) +
                    "; added=" + added.Count +
                    "; removed=" + removedJids.Count +
                    "; ms=" + elapsed.ElapsedMilliseconds +
                    "; completeness=" + delta.Completeness);

                if (persist && chat != null)
                {
                    List<GroupMember> forStore = GroupRosterRules.CopyForStore(touched);
                    List<PersonGroupMembership> memberships =
                        forStore == null ? null : GroupRosterRules.Memberships(group, forStore, _jids);
                    await _writer.SaveChangesAsync(group, forStore, removedJids, memberships, removedKeys)
                        .ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterDemand] Flush failed: " + ex.Message);
                _diagnostics?.RecordException("roster", "flush-failed", ex, "chat=" + DiagnosticJidMask.Apply(group));
            }
        }

        private Task RunLowAsync(Action action)
        {
            return _lowUi != null ? _lowUi.RunLowAsync(action) : _ui.RunAsync(action);
        }

        private ChatItem FindRow(string group)
        {
            List<ChatItem> rows = _rows.FindConversation(group);
            return rows != null && rows.Count > 0 ? rows[0] : _chats.FindChat(group);
        }

        private GroupMember FindOnRow(ChatItem chat, string memberJid)
        {
            if (chat == null || string.IsNullOrWhiteSpace(memberJid))
            {
                return null;
            }

            GroupMember member = chat.FindGroupMember(memberJid);
            if (member != null)
            {
                return member;
            }

            string canonical = _jids.GetCanonicalJid(memberJid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, memberJid, StringComparison.OrdinalIgnoreCase))
            {
                member = chat.FindGroupMember(canonical);
                if (member != null)
                {
                    return member;
                }
            }

            string alias;
            return _jids.TryGetAlias(memberJid, out alias) ? chat.FindGroupMember(alias) : null;
        }

        private void KeepTemplate(string group, GroupMember stored)
        {
            string key = JidHelper.Normalize(stored?.Jid);
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            lock (_gate)
            {
                Dictionary<string, GroupMember> byJid;
                if (!_storedTemplates.TryGetValue(group, out byJid))
                {
                    byJid = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
                    _storedTemplates[group] = byJid;
                }

                byJid[key] = stored;
                string canonical = _jids.GetCanonicalJid(key);
                if (!string.IsNullOrWhiteSpace(canonical))
                {
                    byJid[canonical] = stored;
                }
            }
        }

        private Dictionary<string, GroupMember> TakeTemplates(string group)
        {
            lock (_gate)
            {
                Dictionary<string, GroupMember> byJid;
                if (!_storedTemplates.TryGetValue(group, out byJid))
                {
                    return null;
                }

                _storedTemplates.Remove(group);
                return byJid;
            }
        }

        private GroupMember FromTemplate(Dictionary<string, GroupMember> templates, GroupRosterRecord record)
        {
            if (templates == null)
            {
                return null;
            }

            GroupMember stored;
            if (!templates.TryGetValue(JidHelper.Normalize(record.Jid) ?? string.Empty, out stored) &&
                !templates.TryGetValue(JidHelper.Normalize(record.Lid) ?? string.Empty, out stored) &&
                !templates.TryGetValue(JidHelper.Normalize(record.PhoneNumber) ?? string.Empty, out stored))
            {
                return null;
            }

            GroupMember member = ToMember(record, stored.AvatarUrl);
            member.AvatarFetchedAtUtc = stored.AvatarFetchedAtUtc ?? member.AvatarFetchedAtUtc;
            member.AvatarFetchFailedAtUtc = stored.AvatarFetchFailedAtUtc;
            member.AvatarFetchFailureReason = stored.AvatarFetchFailureReason;
            return member;
        }

        private static GroupMember FindAmong(List<GroupMember> members, GroupRosterRecord record)
        {
            for (int i = 0; i < members.Count; i++)
            {
                if (string.Equals(members[i].Jid, record.Jid, StringComparison.OrdinalIgnoreCase))
                {
                    return members[i];
                }
            }

            return null;
        }

        private static bool MergeOnto(GroupMember live, GroupRosterRecord record)
        {
            bool changed = false;
            if (string.IsNullOrWhiteSpace(live.Lid) && !string.IsNullOrWhiteSpace(record.Lid))
            {
                live.Lid = record.Lid;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(live.PhoneNumber) && !string.IsNullOrWhiteSpace(record.PhoneNumber))
            {
                live.PhoneNumber = record.PhoneNumber;
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(record.DisplayName) &&
                !string.Equals(live.DisplayName, record.DisplayName, StringComparison.Ordinal))
            {
                live.DisplayName = record.DisplayName;
                changed = true;
            }

            if (live.Role != record.Role)
            {
                live.Role = record.Role;
                changed = true;
            }

            return changed;
        }

        private GroupMember ToMember(GroupRosterRecord record, string avatarUrl)
        {
            return new GroupMember
            {
                Jid = record.Jid,
                Lid = record.Lid,
                PhoneNumber = record.PhoneNumber,
                DisplayName = record.DisplayName ?? UsableName(_naming.ResolveMemberDisplayName(record.Jid), record.Jid),
                Role = record.Role,
                AvatarUrl = string.IsNullOrWhiteSpace(avatarUrl) ? null : avatarUrl,
                AvatarFetchedAtUtc = string.IsNullOrWhiteSpace(avatarUrl) ? (DateTime?)null : _utcNow()
            };
        }

        private static string UsableName(string name, string jid)
        {
            return GroupParticipantResolver.IsUsableDisplayLabel(name, jid) ? name.Trim() : null;
        }

        private string CanonicalGroup(string groupJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
            {
                return null;
            }

            string canonical = _jids.GetCanonicalJid(groupJid) ?? JidHelper.Normalize(groupJid);
            return !string.IsNullOrWhiteSpace(canonical) &&
                   canonical.EndsWith(GroupServer, StringComparison.OrdinalIgnoreCase)
                ? canonical
                : null;
        }

        private void Trace(string category, string eventName, string details)
        {
            _diagnostics?.Write(category, eventName, details);
        }
    }
}
