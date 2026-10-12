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

        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            _ = _groupApply.ApplyMetadataAsync(groupJid, _groupMetadata.ReadMetadata(response, groupJid));
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
