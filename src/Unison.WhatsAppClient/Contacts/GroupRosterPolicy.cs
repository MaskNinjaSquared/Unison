// =============================================================================
// GroupRosterPolicy
//
// Idle member-picture work: one batch of GETs, then the next (batch size and
// pauses come from SyncWorkloadStrategy; 16 on High). Not a migration.
//
// ChatAvatarPolicy already does this for chat-list rows. Group members were a
// one-shot of 16 on open that never continued and never remembered a miss, so
// the same people without a photo were asked again every time the group opened.
// AvatarFetchedAtUtc on the member (including no-picture) is the memory;
// this class is the queue.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.WhatsAppClient.Contacts
{
    internal sealed class GroupRosterPolicy
    {
        private static readonly TimeSpan NoPictureRetry = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);

        private readonly SyncWorkloadStrategy _workload;

        private TimeSpan NextBatchDelay => TimeSpan.FromSeconds(_workload.GroupRosterNextBatchDelaySeconds);

        private TimeSpan InterRequestDelay => TimeSpan.FromMilliseconds(_workload.GroupRosterInterRequestDelayMs);

        private int BatchSize => Math.Max(1, _workload.GroupRosterBatchSize);

        private readonly IWhatsAppSessionProvider _sessions;
        private readonly IChatStateStore _chatState;
        private readonly SyncWorkGate _syncWork;
        private readonly IDispatcher _ui;
        private readonly ILowPriorityDispatcher _lowUi;
        private readonly IChatCatalogPersistence _catalog;
        private readonly IGroupService _groups;
        private readonly IContactService _contacts;
        private readonly IJidResolver _jids;

        private readonly object _gate = new object();
        private readonly HashSet<string> _groupsInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _attemptedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _membersFetching = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _nextBatchByGroup =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);
        private int _fullHydrateInFlight;

        internal GroupRosterPolicy(
            IGroupService groups,
            IContactService contacts,
            IJidResolver jids,
            IDispatcher ui,
            IChatCatalogPersistence catalog,
            IChatStateStore chatState,
            SyncWorkGate syncWork,
            IWhatsAppSessionProvider sessions,
            SyncWorkloadStrategy workload)
        {
            _workload = workload ?? throw new ArgumentNullException(nameof(workload));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _syncWork = syncWork ?? throw new ArgumentNullException(nameof(syncWork));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _lowUi = ui as ILowPriorityDispatcher;
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _chatState = chatState ?? throw new ArgumentNullException(nameof(chatState));
            _groups = groups ?? throw new ArgumentNullException(nameof(groups));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
            _jids = jids ?? throw new ArgumentNullException(nameof(jids));
        }

        private Task RunLowAsync(
            Action action,
            [CallerMemberName] string caller = null,
            [CallerFilePath] string callerFile = null)
        {
            return _lowUi != null
                ? _lowUi.RunLowAsync(action, caller, callerFile)
                : _ui.RunAsync(action, caller, callerFile);
        }

        /// <summary>True while a full-roster <see cref="HydrateAsync"/> pass is running.</summary>
        public bool IsHydrating
        {
            get
            {
                lock (_gate)
                {
                    return _fullHydrateInFlight > 0;
                }
            }
        }

        public Task HydrateAsync(string groupJid)
        {
            return HydrateCoreAsync(groupJid, CancellationToken.None);
        }

        /// <summary>
        /// Fetches pictures only for the given member JIDs. No next-batch schedule for the rest
        /// of the roster (visible bubbles on chat open / scroll).
        /// </summary>
        public Task HydrateVisibleAsync(string groupJid, IReadOnlyList<string> memberJids)
        {
            return HydrateVisibleCoreAsync(groupJid, memberJids, CancellationToken.None);
        }

        private async Task HydrateCoreAsync(string groupJid, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || !_sessions.IsTransportReady)
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(groupJid);
            }

            lock (_gate)
            {
                if (!_groupsInFlight.Add(canonical))
                {
                    return;
                }

                _fullHydrateInFlight++;
            }

            try
            {
                string deferReason;
                if (_syncWork.ShouldDeferAvatarFetch(out deferReason))
                {
                    Debug.WriteLine("[GroupRosterPolicy] Deferred " + canonical + ": " + deferReason);
                    ScheduleNext(canonical, "deferred:" + deferReason);
                    return;
                }

                List<GroupMember> batch = await SnapshotBatchAsync(canonical).ConfigureAwait(false);
                if (batch.Count == 0)
                {
                    Debug.WriteLine("[GroupRosterPolicy] No pending member pictures for " + canonical);
                    return;
                }

                Debug.WriteLine("[GroupRosterPolicy] Batch=" + batch.Count + " for " + canonical);

                bool persist = await FetchMembersAsync(canonical, batch, token).ConfigureAwait(false);

                if (persist)
                {
                    _catalog.Schedule();
                }

                if (await HasRemainingAsync(canonical).ConfigureAwait(false))
                {
                    ScheduleNext(canonical, "next-batch");
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterPolicy] Hydrate failed: " + ex.Message);
            }
            finally
            {
                lock (_gate)
                {
                    _groupsInFlight.Remove(canonical);
                    if (_fullHydrateInFlight > 0)
                    {
                        _fullHydrateInFlight--;
                    }
                }
            }
        }

        private async Task HydrateVisibleCoreAsync(
            string groupJid,
            IReadOnlyList<string> memberJids,
            CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(groupJid) ||
                memberJids == null ||
                memberJids.Count == 0 ||
                !_sessions.IsTransportReady)
            {
                return;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(groupJid);
            }

            string deferReason;
            if (_syncWork.ShouldDeferAvatarFetch(out deferReason))
            {
                Debug.WriteLine("[GroupRosterPolicy] Visible deferred " + canonical + ": " + deferReason);
                return;
            }

            List<GroupMember> batch = await SnapshotVisibleBatchAsync(canonical, memberJids).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                return;
            }

            Debug.WriteLine("[GroupRosterPolicy] Visible batch=" + batch.Count + " for " + canonical);

            try
            {
                bool persist = await FetchMembersAsync(canonical, batch, token).ConfigureAwait(false);
                if (persist)
                {
                    _catalog.Schedule();
                }
            }
            catch (TaskCanceledException)
            {
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[GroupRosterPolicy] Visible hydrate failed: " + ex.Message);
            }
        }

        private async Task<bool> FetchMembersAsync(
            string groupJid,
            List<GroupMember> batch,
            CancellationToken token)
        {
            bool persist = false;
            for (int i = 0; i < batch.Count; i++)
            {
                if (token.IsCancellationRequested)
                {
                    break;
                }

                GroupMember member = batch[i];
                string memberKey = MemberKey(member);
                lock (_gate)
                {
                    if (_membersFetching.Contains(memberKey) || _attemptedThisSession.Contains(memberKey))
                    {
                        continue;
                    }

                    _membersFetching.Add(memberKey);
                    _attemptedThisSession.Add(memberKey);
                }

                try
                {
                    GroupMemberAvatarFetchResult result =
                        await _groups.FetchGroupMemberAvatarAsync(member, token)
                            .ConfigureAwait(false);
                    if (result == null)
                    {
                        result = new GroupMemberAvatarFetchResult
                        {
                            IsTransientFailure = true,
                            FailureReason = "empty"
                        };
                    }

                    string applyGroup = groupJid;
                    await RunLowAsync(
                            () => _groups.ApplyGroupMemberAvatarOutcome(
                                member.Jid,
                                result,
                                limitToGroupJid: applyGroup),
                            caller: "ApplyMemberAvatar")
                        .ConfigureAwait(false);

                    if (result.HasPicture)
                    {
                        persist = true;
                        await _contacts.NotifyAvatarCachedAsync(member.Jid, result.LocalUri)
                            .ConfigureAwait(false);
                    }
                    else if (result.IsNotFound)
                    {
                        persist = true;
                    }
                    else
                    {
                        lock (_gate)
                        {
                            _attemptedThisSession.Remove(memberKey);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GroupRosterPolicy] Member " + member.Jid + " failed: " + ex.Message);
                    lock (_gate)
                    {
                        _attemptedThisSession.Remove(memberKey);
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        _membersFetching.Remove(memberKey);
                    }
                }

                if (i < batch.Count - 1)
                {
                    await Task.Delay(InterRequestDelay, token).ConfigureAwait(false);
                }
            }

            return persist;
        }

        private void ScheduleNext(string groupJid, string reason)
        {
            CancellationTokenSource previous = null;
            CancellationTokenSource next = new CancellationTokenSource();
            lock (_gate)
            {
                if (_nextBatchByGroup.TryGetValue(groupJid, out previous))
                {
                    _nextBatchByGroup[groupJid] = next;
                }
                else
                {
                    _nextBatchByGroup.Add(groupJid, next);
                }
            }

            if (previous != null)
            {
                try
                {
                    previous.Cancel();
                    previous.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            CancellationToken token = next.Token;
            Debug.WriteLine("[GroupRosterPolicy] Next batch in " + (int)NextBatchDelay.TotalSeconds +
                            "s for " + groupJid + " (" + reason + ")");

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(NextBatchDelay, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }

                    await HydrateCoreAsync(groupJid, token).ConfigureAwait(false);
                }
                catch (TaskCanceledException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[GroupRosterPolicy] Next batch failed: " + ex.Message);
                }
            });
        }

        private async Task<List<GroupMember>> SnapshotBatchAsync(string groupJid)
        {
            List<GroupMember> snapshot = null;
            await RunLowAsync(() =>
            {
                ChatItem chat = FindGroup(groupJid);
                if (chat?.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    return;
                }

                // Refs only — ShouldFetch filter runs off the UI hop.
                snapshot = new List<GroupMember>(chat.GroupMembers);
            }).ConfigureAwait(false);

            if (snapshot == null || snapshot.Count == 0)
            {
                return new List<GroupMember>();
            }

            DateTime nowUtc = DateTime.UtcNow;
            int batchSize = BatchSize;
            return await Task.Run(() =>
            {
                var batch = new List<GroupMember>();
                for (int i = 0; i < snapshot.Count && batch.Count < batchSize; i++)
                {
                    GroupMember member = snapshot[i];
                    if (ShouldFetch(member, nowUtc))
                    {
                        batch.Add(member);
                    }
                }

                return batch;
            }).ConfigureAwait(false);
        }

        private async Task<List<GroupMember>> SnapshotVisibleBatchAsync(
            string groupJid,
            IReadOnlyList<string> memberJids)
        {
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < memberJids.Count; i++)
            {
                string raw = memberJids[i];
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                string key = _jids.GetCanonicalJid(raw);
                if (string.IsNullOrWhiteSpace(key))
                {
                    key = JidHelper.Normalize(raw);
                }

                if (!string.IsNullOrWhiteSpace(key))
                {
                    wanted.Add(key);
                }

                wanted.Add(raw.Trim());
            }

            var batch = new List<GroupMember>();
            if (wanted.Count == 0)
            {
                return batch;
            }

            await _groups.EnsureMembersAsync(groupJid, memberJids).ConfigureAwait(false);

            DateTime nowUtc = DateTime.UtcNow;
            await RunLowAsync(() =>
            {
                ChatItem chat = FindGroup(groupJid);
                if (chat?.GroupMembers == null)
                {
                    return;
                }

                var seen = new HashSet<GroupMember>();
                foreach (string key in wanted)
                {
                    // Indexed tip lookup — never walk the whole roster with MemberMatches.
                    GroupMember member = chat.FindGroupMember(key);
                    if (member != null && seen.Add(member) && ShouldFetch(member, nowUtc))
                    {
                        batch.Add(member);
                    }
                }
            }).ConfigureAwait(false);

            return batch;
        }

        private async Task<bool> HasRemainingAsync(string groupJid)
        {
            List<GroupMember> snapshot = null;
            await RunLowAsync(() =>
            {
                ChatItem chat = FindGroup(groupJid);
                if (chat?.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    return;
                }

                snapshot = new List<GroupMember>(chat.GroupMembers);
            }).ConfigureAwait(false);

            if (snapshot == null || snapshot.Count == 0)
            {
                return false;
            }

            DateTime nowUtc = DateTime.UtcNow;
            return await Task.Run(() =>
            {
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (ShouldFetch(snapshot[i], nowUtc))
                    {
                        return true;
                    }
                }

                return false;
            }).ConfigureAwait(false);
        }

        private bool ShouldFetch(GroupMember member, DateTime nowUtc)
        {
            if (member == null || string.IsNullOrWhiteSpace(member.Jid))
            {
                return false;
            }

            if (!member.NeedsAvatarLookup(nowUtc, NoPictureRetry, FailureBackoff))
            {
                return false;
            }

            string key = MemberKey(member);
            lock (_gate)
            {
                return !_attemptedThisSession.Contains(key) && !_membersFetching.Contains(key);
            }
        }

        private string MemberKey(GroupMember member)
        {
            string canonical = _jids.GetCanonicalJid(member.Jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(member.Jid);
            }

            return canonical ?? member.Jid;
        }

        private ChatItem FindGroup(string groupJid)
        {
            ChatItem chat = _chatState.FindChat(groupJid);
            if (chat != null)
            {
                return chat;
            }

            string canonical = _jids.GetCanonicalJid(groupJid);
            if (!string.IsNullOrWhiteSpace(canonical) &&
                !string.Equals(canonical, groupJid, StringComparison.OrdinalIgnoreCase))
            {
                return _chatState.FindChat(canonical);
            }

            return null;
        }
    }
}
