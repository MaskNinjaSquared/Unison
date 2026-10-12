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
using Unison.Uwp.Services.WhatsApp.Groups;
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

        private static bool IsGroupIdPlaceholder(string label, string groupJid) =>
            GroupIdPlaceholder.IsIdPlaceholder(label, groupJid);

        /// <summary>
        /// Whether we already hold a usable name for this group, counting the label on the
        /// row as well as the cached one.
        /// </summary>
        /// <remarks>
        /// The row counts because a name can reach it before it reaches the cache. Written
        /// three ways across the group paths and only one consulted the row, so a synced
        /// subject that the blacklist would otherwise hold back could overwrite a good name
        /// through one path and not the other.
        /// </remarks>
        private bool HasMeaningfulGroupLabel(string groupJid, string rowName)
        {
            string cached;
            if (ContactNames.TryGetValue(groupJid, out cached) &&
                IsMeaningfulChatLabel(cached, groupJid, true))
            {
                return true;
            }

            return IsMeaningfulChatLabel(rowName, groupJid, true);
        }

        private string FindGroupRowName(string groupJid)
        {
            return DuplicateChatRowPick.FirstNonBlankName(
                GetChatRowsForCanonicalJid(GetCanonicalJid(groupJid)));
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
            var socket = _socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return;
            }

            try
            {
                Debug.WriteLine("[WhatsAppService] Fetching all participating groups...");
                var response = await socket.QueryParticipatingGroupsAsync();
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

        private async Task PersistGroupRosterAsync(string groupJid, List<GroupMember> members)
        {
            try
            {
                await _groupRosterStore.InitializeAsync().ConfigureAwait(false);
                await _groupRosterStore.ReplaceForGroupAsync(groupJid, members).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Group roster persist failed: " + ex.Message);
            }
        }

        private async Task QueryUnresolvedGroupMetadataAsync(int limit = 25)
        {
            // Capture once: post-replay maintenance can still be running when the session
            // tears the socket down. Re-reading _socket after each await races that clear.
            var socket = _socket;
            if (socket == null || !socket.IsHandshakeComplete) return;

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
            var pending = unresolved.Take(Math.Max(1, limit)).ToList();
            foreach (var chat in pending)
            {
                if (string.IsNullOrWhiteSpace(chat.JID)) continue;

                socket = _socket;
                if (socket == null || !socket.IsHandshakeComplete)
                {
                    Debug.WriteLine("[WhatsAppService] QueryUnresolvedGroupMetadataAsync aborted (socket gone)");
                    break;
                }

                attempts++;

                // One interactive IQ per group with a pause between: on a large account this
                // stretch runs for a while, and it is the last thing standing between the user
                // and correctly named groups. Start at 1 of N (never "0 of N").
                RaiseSyncStatus(
                    SyncPhaseStatus.Format(SyncPhaseStatus.Groups, attempts, pending.Count));

                try
                {
                    var response = await socket.QueryGroupMetadataAsync(chat.JID);
                    string subject = ExtractGroupSubject(response, chat.JID);
                    if (_groupApply.TryCacheSyncedSubject(chat.JID, subject, chat.Name))
                    {
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

            RaiseSyncStatus(null);

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
            _ = _groupApply.ApplyMetadataAsync(groupJid, _groupMetadata.ReadMetadata(response, groupJid));
        }

        private string ExtractGroupSubject(BinaryNode response, string groupJid) =>
            _groupMetadata.ExtractSubject(response, groupJid);

        private async Task ProcessGroupNodes(List<BinaryNode> groupNodes)
        {
            if (groupNodes == null || groupNodes.Count == 0)
            {
                Debug.WriteLine("[WhatsAppService] ProcessGroupNodes: No groups to process.");
                return;
            }

            Debug.WriteLine($"[WhatsAppService] Processing {groupNodes.Count} groups...");

            // Every node is read before a single row is touched; the apply then makes one UI pass.
            await _groupApply.ApplyListingAsync(_groupMetadata.ReadListing(groupNodes));
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

            foreach (string candidate in _groupMetadata.GetPictureCandidates(member))
            {
                token.ThrowIfCancellationRequested();

                ProfilePictureResult result = null;
                using (await _usyncGate.AcquireAsync(token))
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

                if (!string.IsNullOrWhiteSpace(result?.Url))
                {
                    string localUri = await _avatarFetcher.CachePreviewAsync(member.Jid, result.Url, token);
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
                    if (member == null || !GroupRosterRules.MemberMatches(member, canonical, _jids))
                    {
                        continue;
                    }

                    if (result.HasPicture)
                    {
                        ChatAvatarOutcome.RecordCached(member, result.LocalUri, nowUtc);
                    }
                    else if (result.IsNotFound)
                    {
                        ChatAvatarOutcome.RecordAbsent(member, "no-picture", nowUtc);
                    }
                    else
                    {
                        ChatAvatarOutcome.RecordFailure(member, result.FailureReason ?? "fetch-failed", nowUtc);
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

    }
}
