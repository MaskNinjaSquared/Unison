using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Hub row chrome: announce groups first, avatar fallback, chat-detail title pair.
    /// </summary>
    public static class CommunityHubPresentation
    {
        /// <summary>Announcements (Avisos) pinned above other linked groups.</summary>
        public static List<ChatItem> OrderMembers(IReadOnlyList<ChatItem> members)
        {
            if (members == null || members.Count == 0)
            {
                return members == null ? null : new List<ChatItem>();
            }

            return members
                .Where(m => m != null)
                .OrderByDescending(m => m.IsCommunityAnnounce)
                .ThenByDescending(m => m.LastMessageTimestampUtc ?? DateTime.MinValue)
                .ThenBy(m => m.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// When the community parent has no picture yet, borrow the announce group's
        /// (else the first member that has one) so the main list is not a blank plate.
        /// </summary>
        public static void ApplyMemberAvatarFallback(ChatItem hub)
        {
            if (hub == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(hub.AvatarUrl) ||
                !string.IsNullOrWhiteSpace(hub.AvatarHighUrl))
            {
                return;
            }

            IReadOnlyList<ChatItem> members = hub.GetCommunityGroups();
            if (members == null || members.Count == 0)
            {
                return;
            }

            ChatItem source = null;
            for (int i = 0; i < members.Count; i++)
            {
                ChatItem m = members[i];
                if (m != null && m.IsCommunityAnnounce && HasAvatar(m))
                {
                    source = m;
                    break;
                }
            }

            if (source == null)
            {
                for (int i = 0; i < members.Count; i++)
                {
                    ChatItem m = members[i];
                    if (m != null && HasAvatar(m))
                    {
                        source = m;
                        break;
                    }
                }
            }

            if (source == null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(source.AvatarHighUrl))
            {
                hub.AvatarHighUrl = source.AvatarHighUrl;
            }

            if (!string.IsNullOrWhiteSpace(source.AvatarUrl))
            {
                hub.AvatarUrl = source.AvatarUrl;
            }
        }

        /// <summary>
        /// When a linked subgroup tip is newer than the hub strip, copy it onto the hub so
        /// catalogue/visible order can rise without a full CommunityListProjector pass.
        /// Returns true when the hub tip changed.
        /// </summary>
        public static bool ApplyTipIfNewer(ChatItem hub, ChatItem member, IStringResources strings = null)
        {
            if (hub == null || member == null)
            {
                return false;
            }

            DateTime memberTs = member.LastMessageTimestampUtc ?? DateTime.MinValue;
            DateTime hubTs = hub.LastMessageTimestampUtc ?? DateTime.MinValue;
            if (memberTs == DateTime.MinValue || memberTs < hubTs)
            {
                return false;
            }

            if (memberTs == hubTs &&
                string.Equals(hub.LastMessageId, member.LastMessageId, StringComparison.Ordinal))
            {
                return false;
            }

            hub.LastMessage = member.LastMessage;
            hub.LastMessageIsFromMe = member.LastMessageIsFromMe;
            hub.LastMessageSenderName = member.LastMessageSenderName;
            hub.LastMessageParticipantJid = member.LastMessageParticipantJid;
            hub.LastMessageKind = member.LastMessageKind;
            hub.LastMessageSendState = member.LastMessageSendState;
            hub.LastMessageMentionedJids = member.LastMessageMentionedJids;
            hub.LastMessageTimestampUtc = member.LastMessageTimestampUtc;
            hub.LastMessageId = member.LastMessageId;
            hub.Timestamp = member.Timestamp;

            string groupLabel = member.GetNameResolved(strings);
            if (string.IsNullOrWhiteSpace(groupLabel))
            {
                groupLabel = (member.Name ?? string.Empty).Trim();
            }

            if (string.IsNullOrEmpty(groupLabel))
            {
                hub.LastMessageAuthor = member.LastMessageAuthor;
                hub.LastPreview.AuthorDetail = string.Empty;
                hub.LastPreview.ShowAuthorChevron = false;
            }
            else
            {
                hub.LastMessageAuthor = groupLabel;
                hub.LastPreview.AuthorDetail = member.LastMessageAuthor ?? string.Empty;
                hub.LastPreview.ShowAuthorChevron = true;
            }

            return true;
        }

        /// <summary>
        /// Copies a usable hub <see cref="ChatItem.Name"/> onto each member as
        /// <see cref="ChatItem.LinkedParentDisplayName"/> for chat-detail headers.
        /// </summary>
        public static void StampLinkedParentDisplayNames(ChatItem hub)
        {
            if (hub == null || !IsUsableHubLabel(hub.Name, hub.JID))
            {
                return;
            }

            string label = hub.Name.Trim();
            IReadOnlyList<ChatItem> members = hub.GetCommunityGroups();
            if (members == null || members.Count == 0)
            {
                return;
            }

            for (int i = 0; i < members.Count; i++)
            {
                ChatItem member = members[i];
                if (member != null)
                {
                    member.LinkedParentDisplayName = label;
                }
            }
        }

        /// <summary>
        /// Chat-detail header for a linked subgroup: community name on top, group name under it.
        /// </summary>
        /// <param name="resolveParentLabel">
        /// Optional name lookup (push-name / contacts) when the parent row is missing or unnamed.
        /// </param>
        public static bool TryGetDetailHeader(
            ChatItem chat,
            Func<string, ChatItem> resolveParent,
            IStringResources strings,
            out string communityTitle,
            out string groupSubtitle,
            Func<string, string> resolveParentLabel = null)
        {
            communityTitle = null;
            groupSubtitle = null;
            if (chat == null || string.IsNullOrWhiteSpace(chat.LinkedParentJid))
            {
                return false;
            }

            string parentJid = chat.LinkedParentJid;
            communityTitle = PickCommunityTitle(
                chat.LinkedParentDisplayName,
                resolveParent != null ? resolveParent(parentJid) : null,
                resolveParentLabel != null ? resolveParentLabel(parentJid) : null,
                parentJid,
                strings);

            groupSubtitle = chat.GetNameResolved(strings);
            if (string.IsNullOrWhiteSpace(groupSubtitle))
            {
                groupSubtitle = (chat.Name ?? string.Empty).Trim();
            }

            return true;
        }

        /// <summary>Hub / header title: real name first, never the localized “Community” placeholder as a hit.</summary>
        /// <param name="resolveLabel">
        /// Prefer <see cref="Contracts.WhatsApp.IGroupNaming.ResolveChatDisplayName"/> so a cached
        /// group subject wins; contact-only lookup skips <c>GroupSubjects</c> and leaves the fallback.
        /// </param>
        public static string ResolveHubTitle(
            ChatItem hub,
            Func<string, string> resolveLabel,
            IStringResources strings)
        {
            if (hub == null)
            {
                return string.Empty;
            }

            if (IsUsableHubLabel(hub.Name, hub.JID))
            {
                return hub.Name.Trim();
            }

            string resolved = resolveLabel != null ? resolveLabel(hub.JID) : null;
            if (IsUsableHubLabel(resolved, hub.JID))
            {
                return resolved.Trim();
            }

            return strings != null
                ? strings.Get("Chat_CommunityFallbackName", "Community")
                : "Community";
        }

        /// <summary>
        /// Stamps a usable hub <see cref="ChatItem.Name"/> from <paramref name="resolveLabel"/>
        /// (subjects-first) and copies it onto linked members. Returns false when nothing usable.
        /// </summary>
        public static bool TryStampHubLabel(ChatItem hub, Func<string, string> resolveLabel)
        {
            if (hub == null)
            {
                return false;
            }

            if (IsUsableHubLabel(hub.Name, hub.JID))
            {
                StampLinkedParentDisplayNames(hub);
                return true;
            }

            string label = resolveLabel != null ? resolveLabel(hub.JID) : null;
            if (!IsUsableHubLabel(label, hub.JID))
            {
                return false;
            }

            hub.Name = label.Trim();
            StampLinkedParentDisplayNames(hub);
            return true;
        }

        public static bool IsUsableHubLabel(string label, string hubJid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return false;
            }

            string trimmed = label.Trim();
            if (GroupIdPlaceholder.IsIdPlaceholder(trimmed, hubJid))
            {
                return false;
            }

            // Never treat the localized list placeholder as a real subject — otherwise a
            // stamped "Community" / "Comunidade" blocks EnrichCommunityHubLabel probes.
            if (IsCommunityFallbackLabel(trimmed))
            {
                return false;
            }

            return !string.Equals(trimmed, hubJid, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Known en/pt (and common) fallbacks used when the parent subject is still unknown.
        /// </summary>
        private static bool IsCommunityFallbackLabel(string trimmed)
        {
            return string.Equals(trimmed, "Community", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Comunidade", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Comunidad", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Gemeinschaft", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Communauté", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Communiteit", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Społeczność", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Сообщество", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Спільнота", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "Komunitas", StringComparison.OrdinalIgnoreCase);
        }

        private static string PickCommunityTitle(
            string stamped,
            ChatItem parent,
            string resolvedLabel,
            string parentJid,
            IStringResources strings)
        {
            if (IsUsableHubLabel(stamped, parentJid))
            {
                return stamped.Trim();
            }

            if (parent != null && IsUsableHubLabel(parent.Name, parent.JID ?? parentJid))
            {
                return parent.Name.Trim();
            }

            if (IsUsableHubLabel(resolvedLabel, parentJid))
            {
                return resolvedLabel.Trim();
            }

            return strings != null
                ? strings.Get("Chat_CommunityFallbackName", "Community")
                : "Community";
        }

        private static bool HasAvatar(ChatItem chat)
        {
            return chat != null &&
                   (!string.IsNullOrWhiteSpace(chat.AvatarUrl) ||
                    !string.IsNullOrWhiteSpace(chat.AvatarHighUrl));
        }
    }
}
