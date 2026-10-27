// =============================================================================
// ChannelIdentityApply — name (and later picture) onto a @newsletter ChatItem.
// Uses existing ChatItem.Name / AvatarUrl / AvatarPictureId columns — same idea
// as Status author enrichment from Person, without a separate channel store.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class ChannelIdentityApply
    {
        /// <summary>
        /// Writes a usable channel title onto the row when the current name is blank
        /// or still a raw JID placeholder. Returns true when <see cref="ChatItem.Name"/> changed.
        /// </summary>
        public static bool TryApplyName(ChatItem chat, string candidate)
        {
            if (chat == null || !chat.IsChannel)
            {
                return false;
            }

            string next = NormalizeLabel(candidate, chat.JID);
            if (string.IsNullOrWhiteSpace(next))
            {
                return false;
            }

            string current = chat.Name;
            if (!string.IsNullOrWhiteSpace(current) &&
                !IsJidLikePlaceholder(current, chat.JID) &&
                !string.Equals(current.Trim(), next, StringComparison.Ordinal))
            {
                // Keep a human title already on the row; do not clobber with push/notify noise.
                return false;
            }

            if (string.Equals(current, next, StringComparison.Ordinal))
            {
                return false;
            }

            chat.Name = next;
            return true;
        }

        /// <summary>
        /// Stores a remote picture id / path on the existing avatar fields when missing.
        /// Local download stays on the contact/avatar façades.
        /// </summary>
        public static bool TryApplyPictureHint(ChatItem chat, string pictureId, string remoteUrl)
        {
            if (chat == null || !chat.IsChannel)
            {
                return false;
            }

            bool changed = false;
            if (!string.IsNullOrWhiteSpace(pictureId) &&
                !string.Equals(chat.AvatarPictureId, pictureId, StringComparison.Ordinal))
            {
                chat.AvatarPictureId = pictureId.Trim();
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(remoteUrl) &&
                string.IsNullOrWhiteSpace(chat.AvatarUrl) &&
                string.IsNullOrWhiteSpace(chat.AvatarRemoteUrl))
            {
                chat.AvatarRemoteUrl = remoteUrl.Trim();
                changed = true;
            }

            return changed;
        }

        private static string NormalizeLabel(string candidate, string jid)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            string trimmed = candidate.Trim();
            if (IsJidLikePlaceholder(trimmed, jid))
            {
                return null;
            }

            return trimmed;
        }

        private static bool IsJidLikePlaceholder(string label, string jid)
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                return true;
            }

            if (label.IndexOf('@') >= 0)
            {
                return true;
            }

            string normalized = JidHelper.Normalize(jid);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            int at = normalized.IndexOf('@');
            string user = at > 0 ? normalized.Substring(0, at) : normalized;
            return string.Equals(label.Trim(), user, StringComparison.OrdinalIgnoreCase);
        }
    }
}
