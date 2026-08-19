using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private bool HasGroupChats()
        {
            return Chats.Any(c => c != null && (c.IsGroup || IsGroupJid(c.JID)));
        }

        private bool IsGroupJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   NormalizeJid(jid).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// True when a group label is just the chat id: <c>120363…</c> or the legacy
        /// <c>phone-timestamp</c> user part. Those are placeholders, not subjects.
        /// </summary>
        private static bool IsGroupIdPlaceholder(string label, string groupJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            string trimmed = label.Trim();
            if (trimmed.Contains("@"))
            {
                return true;
            }

            string bare = (groupJid ?? string.Empty).Split('@')[0];
            if (!string.IsNullOrEmpty(bare) &&
                string.Equals(trimmed, bare, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (trimmed.All(char.IsDigit))
            {
                return true;
            }

            string labelDigits = ExtractDigitsOnly(trimmed);
            string jidDigits = ExtractDigitsOnly(bare);
            bool hasLetters = trimmed.Any(char.IsLetter);
            return !hasLetters &&
                   jidDigits.Length >= 7 &&
                   string.Equals(labelDigits, jidDigits, StringComparison.Ordinal);
        }

        public Task QueryAllGroupsAsync() => QueryAllGroupsAsync(false);

        /// <param name="force">
        /// Ignores the reuse window. For the callers that only ask because a group is still
        /// showing its JID - there is nothing to gain by making the user wait out a window that
        /// exists to stop redundant passes, and this pass is not redundant.
        /// </param>
        public async Task QueryAllGroupsAsync(bool force)
        {
            if (ShouldDeferReconnectReplayWork())
            {
                Debug.WriteLine("[WhatsAppService] QueryAllGroupsAsync skipped (replay drain active)");
                return;
            }

            string syncTrafficDeferReason;
            if (ShouldDeferProfilePictureFetch(out syncTrafficDeferReason))
            {
                Debug.WriteLine($"[WhatsAppService] QueryAllGroupsAsync skipped (sync traffic active: {syncTrafficDeferReason})");
                return;
            }

            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                Debug.WriteLine("[WhatsAppService] QueryAllGroupsAsync skipped (handshake not complete)");
                return;
            }

            // Five separate callers ask for this - name resolution, the background pass, avatar
            // fallback, opening a group - and they overlap. Each pass costs one participating
            // query plus up to twenty-five interactive metadata queries, so two overlapping
            // passes were enough to keep the socket answering group queries while everything
            // else timed out waiting behind them. The group list does not change by the second.
            var sinceLastPass = DateTime.UtcNow - _lastGroupQueryUtc;
            if (!force && sinceLastPass < GroupQueryReuseWindow)
            {
                Debug.WriteLine(
                    "[WhatsAppService] QueryAllGroupsAsync skipped (last pass was " +
                    sinceLastPass.TotalSeconds.ToString("F0") + "s ago)");
                return;
            }

            // The window is armed on the way out, not on the way in. Arming it first meant a
            // listing that timed out - which is exactly when the groups are still nameless -
            // bought itself two minutes of silence before anything could try again.
            bool listingAnswered = false;

            try
            {
                Debug.WriteLine("[WhatsAppService] Fetching all participating groups...");
                var response = await _socket.QueryParticipatingGroupsAsync();
                if (response != null)
                {
                    listingAnswered = true;

                    // Use recursive search for group nodes
                    var groupNodes = response.FindAllDescendants("group");
                    Debug.WriteLine($"[WhatsAppService] QueryAllGroupsAsync found {groupNodes.Count} 'group' nodes in response.");

                    if (groupNodes.Count == 0)
                    {
                        // Fallback to top-level children if FindAllDescendants failed
                        var topTags = string.Join(", ", response.Children.Select(c => c.Tag));
                        Debug.WriteLine($"[WhatsAppService] No 'group' nodes found. Top tags: [{topTags}]");
                    }

                    await ProcessGroupNodes(groupNodes);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group query failed: {ex.Message}");
            }

            // Deliberately outside the block above. The per-group fallback is what names the
            // groups the listing missed, so a listing that failed is the case it exists for -
            // and it used to be skipped in exactly that case, because both shared one try.
            try
            {
                await QueryUnresolvedGroupMetadataAsync(limit: 25);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] Group metadata fallback failed: {ex.Message}");
            }

            if (listingAnswered)
            {
                _lastGroupQueryUtc = DateTime.UtcNow;
            }
        }

        private async Task RefreshGroupSendPermissionsAsync(string groupJid)
        {
            if (_socket == null || !_socket.IsHandshakeComplete)
            {
                return;
            }

            string canonical = GetCanonicalJid(groupJid);
            if (string.IsNullOrWhiteSpace(canonical) ||
                !canonical.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            try
            {
                var response = await _socket.QueryGroupMetadataAsync(canonical);
                await ApplyGroupMetadataFromResponseAsync(response, canonical, hydrateAvatars: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[WhatsAppService] RefreshGroupSendPermissionsAsync failed for {canonical}: {ex.Message}");
            }
        }

        private async Task QueryUnresolvedGroupMetadataAsync(int limit = 25)
        {
            if (_socket == null || !_socket.IsHandshakeComplete) return;

            var unresolved = new List<ChatItem>();
            await RunOnUiThreadAsync(() =>
            {
                foreach (var c in Chats)
                {
                    if (c == null) continue;
                    bool isGroupChat = c.IsGroup || (!string.IsNullOrEmpty(c.JID) && c.JID.EndsWith("@g.us", StringComparison.OrdinalIgnoreCase));
                    if (!isGroupChat) continue;

                    bool unresolvedName = !IsMeaningfulChatLabel(c.Name, c.JID, true);
                    if (unresolvedName)
                    {
                        unresolved.Add(c);
                    }
                }
            });

            if (unresolved.Count == 0) return;

            int attempts = 0;
            int resolved = 0;
            foreach (var chat in unresolved.Take(Math.Max(1, limit)))
            {
                if (string.IsNullOrWhiteSpace(chat.JID)) continue;
                attempts++;

                try
                {
                    var response = await _socket.QueryGroupMetadataAsync(chat.JID);
                    string subject = ExtractGroupSubject(response, chat.JID);
                    if (!string.IsNullOrWhiteSpace(subject) &&
                        !IsGroupIdPlaceholder(subject, chat.JID))
                    {
                        ContactNames[chat.JID] = subject;
                        resolved++;
                    }

                    ApplyGroupSendPermissionsFromMetadata(response, chat.JID);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[WhatsAppService] QueryGroupMetadataAsync failed for {chat.JID}: {ex.Message}");
                }

                await Task.Delay(120);
            }

            if (resolved > 0)
            {
                await ApplyResolvedNamesToChatsAsync();
                SchedulePersist();
            }

            Debug.WriteLine($"[WhatsAppService] Group metadata fallback: resolved {resolved}/{attempts} unresolved group names");
        }

        /// <summary>
        /// Reads announce-only + current user admin rank from a w:g2 group metadata IQ
        /// and updates the matching chat (Baileys: announcement child, participant admin attr).
        /// Does not fetch member pictures — listing/receipt paths must not fan out IQs.
        /// </summary>
        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            _ = ApplyGroupMetadataFromResponseAsync(response, groupJid, hydrateAvatars: false);
        }

        private async Task ApplyGroupMetadataFromResponseAsync(
            BinaryNode response,
            string groupJid,
            bool hydrateAvatars)
        {
            if (response == null || string.IsNullOrWhiteSpace(groupJid))
            {
                return;
            }

            BinaryNode groupNode = FindGroupNode(response, groupJid);
            if (groupNode == null)
            {
                return;
            }

            bool announceOnly = groupNode.GetChild("announcement") != null;
            GroupParticipantRole myRole = ResolveMyGroupRole(groupNode);
            string canonical = GetCanonicalJid(groupJid);

            await RunOnUiThreadAsync(() =>
            {
                ChatItem chat = Chats.FirstOrDefault(c =>
                    c != null &&
                    string.Equals(GetCanonicalJid(c.JID), canonical, StringComparison.OrdinalIgnoreCase));
                if (chat == null)
                {
                    return;
                }

                if (!chat.IsGroup)
                {
                    chat.IsGroup = true;
                }

                chat.IsAnnounceOnly = announceOnly;
                chat.MyGroupRole = myRole;
                int memberCount = CountGroupMembers(groupNode);
                if (memberCount > 0)
                {
                    chat.GroupMemberCount = memberCount;
                }

                ApplyGroupMembersToChat(chat, ReadGroupMemberDrafts(groupNode));
                SchedulePersist();
            });

            if (hydrateAvatars && _contactService != null)
            {
                await _contactService.HydrateGroupMemberAvatarsAsync(canonical);
            }
        }

        private string ExtractGroupSubject(BinaryNode response, string groupJid)
        {
            if (response == null) return null;

            var groups = response.FindAllDescendants("group");
            foreach (var g in groups)
            {
                if (g?.Attrs == null) continue;
                g.Attrs.TryGetValue("id", out var id);
                g.Attrs.TryGetValue("subject", out var subject);
                if (!string.IsNullOrWhiteSpace(subject) &&
                    (string.IsNullOrWhiteSpace(id) || string.Equals(NormalizeJid(id), NormalizeJid(groupJid), StringComparison.OrdinalIgnoreCase)))
                {
                    return subject;
                }
            }

            var directGroup = response.GetChild("group");
            if (directGroup?.Attrs != null && directGroup.Attrs.TryGetValue("subject", out var directSubject) && !string.IsNullOrWhiteSpace(directSubject))
            {
                return directSubject;
            }

            return null;
        }

        private async Task ProcessGroupNodes(List<BinaryNode> groupNodes)
        {
            if (groupNodes == null || groupNodes.Count == 0)
            {
                Debug.WriteLine("[WhatsAppService] ProcessGroupNodes: No groups to process.");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Processing {groupNodes.Count} groups...");

            // Every node is read before a single row is touched. The listing answers for all of
            // the account's groups at once, so doing this a group at a time meant one hop to the
            // UI thread and one walk of the chat list each - hundreds of both, back to back,
            // while the list was trying to render the sync that provoked the query.
            var parsed = new Dictionary<string, GroupListingEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in groupNodes)
            {
                if (g?.Attrs == null || !g.Attrs.TryGetValue("id", out var id) || string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var jid = id.Contains("@") ? id : id + "@g.us";
                g.Attrs.TryGetValue("subject", out var subject);

                parsed[GetCanonicalJid(NormalizeJid(jid))] = new GroupListingEntry
                {
                    Jid = jid,
                    Subject = subject,
                    AnnounceOnly = g.GetChild("announcement") != null,
                    MyRole = ResolveMyGroupRole(g),
                    MemberCount = CountGroupMembers(g),
                    Members = ReadGroupMemberDrafts(g)
                };
            }

            if (parsed.Count == 0)
            {
                return;
            }

            await RunOnUiThreadAsync(() =>
            {
                foreach (var entry in parsed.Values)
                {
                    if (!string.IsNullOrWhiteSpace(entry.Subject) &&
                        !IsGroupIdPlaceholder(entry.Subject, entry.Jid))
                    {
                        ContactNames[entry.Jid] = entry.Subject;
                        Debug.WriteLine($"[WhatsAppService] Group resolved: {entry.Jid} -> {entry.Subject}");
                    }
                }

                foreach (var chat in Chats)
                {
                    if (chat == null)
                    {
                        continue;
                    }

                    GroupListingEntry entry;
                    if (!parsed.TryGetValue(GetCanonicalJid(chat.JID), out entry))
                    {
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Subject) &&
                        !IsGroupIdPlaceholder(entry.Subject, entry.Jid))
                    {
                        string resolved = ResolveDisplayName(chat.JID, "chat");
                        bool incomingMeaningful = IsMeaningfulChatLabel(resolved, chat.JID, true);
                        bool existingMeaningful = IsMeaningfulChatLabel(chat.Name, chat.JID, true);
                        if (incomingMeaningful || !existingMeaningful)
                        {
                            chat.Name = resolved;
                        }
                    }

                    if (!chat.IsGroup)
                    {
                        chat.IsGroup = true;
                    }

                    chat.IsAnnounceOnly = entry.AnnounceOnly;
                    chat.MyGroupRole = entry.MyRole;
                    if (entry.MemberCount > 0)
                    {
                        chat.GroupMemberCount = entry.MemberCount;
                    }

                    ApplyGroupMembersToChat(chat, entry.Members);
                }

                SchedulePersist();
            });
        }

        /// <summary>What a group listing says about one group, read off the wire.</summary>
        private sealed class GroupListingEntry
        {
            public string Jid;
            public string Subject;
            public bool AnnounceOnly;
            public GroupParticipantRole MyRole;
            public int MemberCount;
            public List<GroupMemberDraft> Members;
        }

        private sealed class GroupMemberDraft
        {
            public string Jid;
            public string PhoneNumber;
            public string Lid;
            public GroupParticipantRole Role;
        }

        private List<GroupMemberDraft> ReadGroupMemberDrafts(BinaryNode groupNode)
        {
            var drafts = new List<GroupMemberDraft>();
            if (groupNode == null)
            {
                return drafts;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BinaryNode participantNode in groupNode.GetChildren("participant"))
            {
                if (participantNode?.Attrs == null)
                {
                    continue;
                }

                string jid = participantNode.Attrs.GetDictionaryValueOrDefault("jid", string.Empty);
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string canonical = NormalizeJid(jid);
                if (string.IsNullOrWhiteSpace(canonical) || !seen.Add(canonical))
                {
                    continue;
                }

                string admin = participantNode.Attrs.GetDictionaryValueOrDefault("admin", string.Empty);
                if (string.IsNullOrWhiteSpace(admin))
                {
                    admin = participantNode.Attrs.GetDictionaryValueOrDefault("type", string.Empty);
                }

                drafts.Add(new GroupMemberDraft
                {
                    Jid = canonical,
                    PhoneNumber = participantNode.Attrs.GetDictionaryValueOrDefault("phone_number", string.Empty),
                    Lid = participantNode.Attrs.GetDictionaryValueOrDefault("lid", string.Empty),
                    Role = ParseParticipantAdminRole(admin)
                });

                if (drafts.Count >= MaxPersistedGroupMembers)
                {
                    break;
                }
            }

            return drafts;
        }

        private void ApplyGroupMembersToChat(ChatItem chat, List<GroupMemberDraft> drafts)
        {
            if (chat == null || drafts == null || drafts.Count == 0)
            {
                return;
            }

            Dictionary<string, GroupMember> previous = null;
            List<GroupMember> previousList = null;
            if (chat.GroupMembers != null && chat.GroupMembers.Count > 0)
            {
                previous = new Dictionary<string, GroupMember>(StringComparer.OrdinalIgnoreCase);
                previousList = new List<GroupMember>(chat.GroupMembers.Count);
                foreach (var existing in chat.GroupMembers)
                {
                    if (existing == null || string.IsNullOrWhiteSpace(existing.Jid))
                    {
                        continue;
                    }

                    previous[existing.Jid] = existing;
                    previousList.Add(existing);
                }
            }

            var next = new List<GroupMember>(drafts.Count);
            foreach (var draft in drafts)
            {
                if (draft == null || string.IsNullOrWhiteSpace(draft.Jid))
                {
                    continue;
                }

                GroupMember prior = FindPreviousGroupMember(previous, previousList, draft);
                string avatarUrl = prior?.AvatarUrl ?? FindExistingAvatarUrl(draft.Jid, draft.PhoneNumber, draft.Lid);
                DateTime? fetchedAt = prior?.AvatarFetchedAtUtc;
                if (!string.IsNullOrWhiteSpace(avatarUrl) && !fetchedAt.HasValue)
                {
                    fetchedAt = DateTime.UtcNow;
                }

                next.Add(new GroupMember
                {
                    Jid = draft.Jid,
                    PhoneNumber = string.IsNullOrWhiteSpace(draft.PhoneNumber) ? null : draft.PhoneNumber,
                    Lid = string.IsNullOrWhiteSpace(draft.Lid) ? null : draft.Lid,
                    Role = draft.Role,
                    DisplayName = ResolveGroupMemberDisplayName(draft.Jid),
                    AvatarUrl = avatarUrl,
                    AvatarFetchedAtUtc = fetchedAt,
                    AvatarFetchFailedAtUtc = prior?.AvatarFetchFailedAtUtc,
                    AvatarFetchFailureReason = prior?.AvatarFetchFailureReason
                });
            }

            if (next.Count == 0)
            {
                return;
            }

            chat.GroupMembers = next;
            if (chat.GroupMemberCount < next.Count)
            {
                chat.GroupMemberCount = next.Count;
            }

            SchedulePersistGroupMemberships(chat.JID, next);
        }

        private GroupMember FindPreviousGroupMember(
            Dictionary<string, GroupMember> byJid,
            List<GroupMember> previousList,
            GroupMemberDraft draft)
        {
            if (draft == null)
            {
                return null;
            }

            GroupMember prior;
            if (byJid != null && byJid.TryGetValue(draft.Jid, out prior))
            {
                return prior;
            }

            if (previousList == null || previousList.Count == 0)
            {
                return null;
            }

            string canonical = GetCanonicalJid(draft.Jid);
            for (int i = 0; i < previousList.Count; i++)
            {
                GroupMember existing = previousList[i];
                if (MemberMatchesJid(existing, canonical))
                {
                    return existing;
                }

                if (!string.IsNullOrWhiteSpace(draft.Lid) &&
                    MemberMatchesJid(existing, GetCanonicalJid(draft.Lid)))
                {
                    return existing;
                }

                if (!string.IsNullOrWhiteSpace(draft.PhoneNumber) &&
                    MemberMatchesJid(existing, GetCanonicalJid(NormalizeJid(
                        draft.PhoneNumber.IndexOf('@') >= 0
                            ? draft.PhoneNumber
                            : draft.PhoneNumber + "@s.whatsapp.net"))))
                {
                    return existing;
                }
            }

            return null;
        }

        private void SchedulePersistGroupMemberships(string groupJid, List<GroupMember> members)
        {
            if (_personStore == null || string.IsNullOrWhiteSpace(groupJid) || members == null)
            {
                return;
            }

            // Write LID + PN aliases so groups-in-common lookup works regardless of which JID the UI holds.
            var snapshot = new List<PersonGroupMembership>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                if (member == null)
                {
                    continue;
                }

                foreach (string personJid in EnumerateMembershipPersonKeys(member))
                {
                    string dedupe = personJid + "\u001f" + groupJid;
                    if (!seen.Add(dedupe))
                    {
                        continue;
                    }

                    snapshot.Add(new PersonGroupMembership
                    {
                        PersonJid = personJid,
                        GroupJid = groupJid,
                        Role = member.Role
                    });
                }
            }

            if (snapshot.Count == 0)
            {
                return;
            }

            _ = PersistGroupMembershipsAsync(groupJid, snapshot);
        }

        private async Task PersistGroupMembershipsAsync(string groupJid, List<PersonGroupMembership> members)
        {
            try
            {
                await _personStore.InitializeAsync().ConfigureAwait(false);
                await _personStore.ReplaceGroupMembershipsAsync(groupJid, members).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] PersonGroup persist failed: " + ex.Message);
            }
        }

        private string ResolveGroupMemberDisplayName(string jid)
        {
            if (IsSelfLinkedJid(jid))
            {
                return SelfListDisplayName();
            }

            string resolved = ResolveDisplayName(jid, "chat");
            if (!string.IsNullOrWhiteSpace(resolved) && IsMeaningfulChatLabel(resolved, jid, false))
            {
                return resolved;
            }

            return GetResolvedName(jid) ?? jid;
        }

        private bool JidsMatchCanonical(string jid, string canonical)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return false;
            }

            return string.Equals(
                GetCanonicalJid(NormalizeJid(jid)),
                canonical,
                StringComparison.OrdinalIgnoreCase);
        }

        private List<string> GetGroupMemberPictureCandidates(GroupMember member)
        {
            var candidates = new List<string>();
            Action<string> add = value =>
            {
                string normalized = NormalizeJid(value);
                if (!string.IsNullOrWhiteSpace(normalized) &&
                    !candidates.Contains(normalized, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(normalized);
                }
            };

            if (member == null)
            {
                return candidates;
            }

            // PN first: LID picture IQs often 404 and used to burn the only attempt.
            add(member.PhoneNumber);
            add(GetCanonicalJid(member.PhoneNumber));
            add(GetCanonicalJid(member.Jid));
            add(member.Jid);
            add(member.Lid);
            add(GetCanonicalJid(member.Lid));
            return candidates;
        }

        public Task<GroupMemberAvatarFetchResult> FetchGroupMemberAvatarAsync(
            GroupMember member,
            CancellationToken token)
        {
            return FetchAndCacheGroupMemberAvatarAsync(member, token);
        }

        private async Task<GroupMemberAvatarFetchResult> FetchAndCacheGroupMemberAvatarAsync(
            GroupMember member,
            CancellationToken token)
        {
            if (member == null || _socket == null || !_socket.IsHandshakeComplete)
            {
                return new GroupMemberAvatarFetchResult
                {
                    IsTransientFailure = true,
                    FailureReason = "socket-not-ready"
                };
            }

            bool sawNotFound = false;
            bool sawTransient = false;
            string lastReason = null;

            foreach (string candidate in GetGroupMemberPictureCandidates(member))
            {
                token.ThrowIfCancellationRequested();

                ProfilePictureResult result = null;
                await _usyncLock.WaitAsync(token);
                try
                {
                    if (_socket == null || !_socket.IsHandshakeComplete)
                    {
                        return new GroupMemberAvatarFetchResult
                        {
                            IsTransientFailure = true,
                            FailureReason = "socket-not-ready"
                        };
                    }

                    result = await _socket.GetProfilePictureUrlResultAsync(candidate, "preview");
                }
                finally
                {
                    _usyncLock.Release();
                }

                if (!string.IsNullOrWhiteSpace(result?.Url))
                {
                    string localUri = await DownloadAndCacheAvatarAsync(
                        member.Jid,
                        result.Url,
                        token);
                    if (!string.IsNullOrWhiteSpace(localUri))
                    {
                        return new GroupMemberAvatarFetchResult { LocalUri = localUri };
                    }

                    sawTransient = true;
                    lastReason = "download:empty";
                    continue;
                }

                if (result != null && result.IsNotFound)
                {
                    sawNotFound = true;
                    lastReason = "no-picture";
                    continue;
                }

                sawTransient = true;
                if (result != null && result.IsTimeout)
                {
                    lastReason = "timeout";
                }
                else
                {
                    lastReason = result?.FailureReason ?? "no-url";
                }
            }

            if (sawNotFound && !sawTransient)
            {
                return new GroupMemberAvatarFetchResult
                {
                    IsNotFound = true,
                    FailureReason = "no-picture"
                };
            }

            return new GroupMemberAvatarFetchResult
            {
                IsTransientFailure = true,
                FailureReason = lastReason ?? "no-url"
            };
        }

        public void ApplyGroupMemberAvatarOutcome(string memberJid, GroupMemberAvatarFetchResult result)
        {
            if (string.IsNullOrWhiteSpace(memberJid) || result == null)
            {
                return;
            }

            DateTime nowUtc = DateTime.UtcNow;
            string canonical = GetCanonicalJid(NormalizeJid(memberJid));
            foreach (var chat in Chats)
            {
                if (chat?.GroupMembers == null || chat.GroupMembers.Count == 0)
                {
                    continue;
                }

                foreach (var member in chat.GroupMembers)
                {
                    if (member == null || !MemberMatchesJid(member, canonical))
                    {
                        continue;
                    }

                    if (result.HasPicture)
                    {
                        member.AvatarUrl = result.LocalUri;
                        member.AvatarFetchedAtUtc = nowUtc;
                        member.AvatarFetchFailedAtUtc = null;
                        member.AvatarFetchFailureReason = null;
                    }
                    else if (result.IsNotFound)
                    {
                        member.AvatarFetchedAtUtc = nowUtc;
                        member.AvatarFetchFailedAtUtc = null;
                        member.AvatarFetchFailureReason = "no-picture";
                    }
                    else
                    {
                        member.AvatarFetchFailedAtUtc = nowUtc;
                        member.AvatarFetchFailureReason = result.FailureReason ?? "fetch-failed";
                    }
                }
            }
        }

        private void StampGroupMemberAvatars(string memberJid, string localUri)
        {
            if (string.IsNullOrWhiteSpace(memberJid) || string.IsNullOrWhiteSpace(localUri))
            {
                return;
            }

            ApplyGroupMemberAvatarOutcome(
                memberJid,
                new GroupMemberAvatarFetchResult { LocalUri = localUri });
        }

        private static List<GroupMember> CloneGroupMembers(IList<GroupMember> source)
        {
            if (source == null || source.Count == 0)
            {
                return null;
            }

            var clone = new List<GroupMember>(source.Count);
            foreach (var member in source)
            {
                if (member == null)
                {
                    continue;
                }

                clone.Add(new GroupMember
                {
                    Jid = member.Jid,
                    PhoneNumber = member.PhoneNumber,
                    Lid = member.Lid,
                    DisplayName = member.DisplayName,
                    Role = member.Role,
                    AvatarUrl = member.AvatarUrl,
                    AvatarFetchedAtUtc = member.AvatarFetchedAtUtc,
                    AvatarFetchFailedAtUtc = member.AvatarFetchFailedAtUtc,
                    AvatarFetchFailureReason = member.AvatarFetchFailureReason
                });
            }

            return clone.Count == 0 ? null : clone;
        }

        private static int CountGroupMembers(BinaryNode groupNode)
        {
            if (groupNode == null)
            {
                return 0;
            }

            int listed = 0;
            List<BinaryNode> participants = groupNode.GetChildren("participant");
            if (participants != null)
            {
                listed = participants.Count;
            }

            int size;
            if (int.TryParse(groupNode.GetAttribute("size"), out size) && size > listed)
            {
                return size;
            }

            return listed;
        }
    }
}
