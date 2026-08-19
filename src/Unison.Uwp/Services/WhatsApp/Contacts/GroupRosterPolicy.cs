// =============================================================================
// GroupRosterPolicy
//
// Idle member-picture work: 16 GETs, then the next 16. Not a migration.
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
using System.Threading;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Uwp.Services.WhatsApp.Contacts
{
    internal sealed class GroupRosterPolicy
    {
        private static readonly TimeSpan NextBatchDelay = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan InterRequestDelay = TimeSpan.FromMilliseconds(900);
        private static readonly TimeSpan NoPictureRetry = TimeSpan.FromDays(7);
        private static readonly TimeSpan FailureBackoff = TimeSpan.FromMinutes(30);
        private const int BatchSize = 16;

        private readonly IWhatsAppService _whatsAppService;
        private readonly IContactService _contacts;

        private readonly object _gate = new object();
        private readonly HashSet<string> _groupsInFlight = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _attemptedThisSession = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> _nextBatchByGroup =
            new Dictionary<string, CancellationTokenSource>(StringComparer.OrdinalIgnoreCase);

        internal GroupRosterPolicy(IWhatsAppService whatsAppService, IContactService contacts)
        {
            _whatsAppService = whatsAppService ?? throw new ArgumentNullException(nameof(whatsAppService));
            _contacts = contacts ?? throw new ArgumentNullException(nameof(contacts));
        }

        public Task HydrateAsync(string groupJid)
        {
            return HydrateCoreAsync(groupJid, CancellationToken.None);
        }

        private async Task HydrateCoreAsync(string groupJid, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(groupJid) || !_whatsAppService.IsTransportReady)
            {
                return;
            }

            string canonical = _whatsAppService.GetCanonicalJid(groupJid);
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
            }

            try
            {
                string deferReason;
                if (_whatsAppService.ShouldDeferAvatarFetch(out deferReason))
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
                        _attemptedThisSession.Add(memberKey);
                    }

                    try
                    {
                        GroupMemberAvatarFetchResult result =
                            await _whatsAppService.FetchGroupMemberAvatarAsync(member, token)
                                .ConfigureAwait(false);
                        if (result == null)
                        {
                            result = new GroupMemberAvatarFetchResult
                            {
                                IsTransientFailure = true,
                                FailureReason = "empty"
                            };
                        }

                        await _whatsAppService.RunOnUiThreadAsync(
                            () => _whatsAppService.ApplyGroupMemberAvatarOutcome(member.Jid, result))
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

                    if (i < batch.Count - 1)
                    {
                        await Task.Delay(InterRequestDelay, token).ConfigureAwait(false);
                    }
                }

                if (persist)
                {
                    _whatsAppService.SchedulePersistPublic();
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
                }
            }
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
            var batch = new List<GroupMember>();
            DateTime nowUtc = DateTime.UtcNow;
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                ChatItem chat = FindGroup(groupJid);
                if (chat?.GroupMembers == null)
                {
                    return;
                }

                for (int i = 0; i < chat.GroupMembers.Count && batch.Count < BatchSize; i++)
                {
                    GroupMember member = chat.GroupMembers[i];
                    if (!ShouldFetch(member, nowUtc))
                    {
                        continue;
                    }

                    batch.Add(member);
                }
            }).ConfigureAwait(false);

            return batch;
        }

        private async Task<bool> HasRemainingAsync(string groupJid)
        {
            bool remaining = false;
            DateTime nowUtc = DateTime.UtcNow;
            await _whatsAppService.RunOnUiThreadAsync(() =>
            {
                ChatItem chat = FindGroup(groupJid);
                if (chat?.GroupMembers == null)
                {
                    return;
                }

                for (int i = 0; i < chat.GroupMembers.Count; i++)
                {
                    if (ShouldFetch(chat.GroupMembers[i], nowUtc))
                    {
                        remaining = true;
                        return;
                    }
                }
            }).ConfigureAwait(false);

            return remaining;
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
                return !_attemptedThisSession.Contains(key);
            }
        }

        private string MemberKey(GroupMember member)
        {
            string canonical = _whatsAppService.GetCanonicalJid(member.Jid);
            if (string.IsNullOrWhiteSpace(canonical))
            {
                canonical = JidHelper.Normalize(member.Jid);
            }

            return canonical ?? member.Jid;
        }

        private ChatItem FindGroup(string groupJid)
        {
            foreach (ChatItem chat in _whatsAppService.Chats)
            {
                if (chat == null)
                {
                    continue;
                }

                if (string.Equals(
                    _whatsAppService.GetCanonicalJid(chat.JID),
                    groupJid,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return chat;
                }
            }

            return null;
        }
    }
}
