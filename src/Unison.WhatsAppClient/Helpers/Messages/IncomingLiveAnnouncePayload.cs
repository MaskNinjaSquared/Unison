// =============================================================================
// IncomingLiveAnnouncePayload
//
// Snapshot for toast / badge / tile after the list strip is already applied.
// Built on the pump thread from values copied off ChatItem, then handed to a
// background Task so WinRT notify work never holds stage=route.
// =============================================================================
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class IncomingLiveAnnouncePayload
    {
        public string ChatJid { get; private set; }

        public string ChatName { get; private set; }

        public string SenderName { get; private set; }

        public string Preview { get; private set; }

        public bool IsGroup { get; private set; }

        public bool IsMuted { get; private set; }

        public bool SuppressToast { get; private set; }

        public int TotalUnread { get; private set; }

        public string AvatarUrl { get; private set; }

        public int ChatUnread { get; private set; }

        /// <summary>Community Avisos — toast uses the megaphone asset, not the row photo.</summary>
        public bool IsCommunityAnnounce { get; private set; }

        /// <summary>
        /// Null when from-me (gate) or the jid is blank. Callers must not touch
        /// <paramref name="chat"/> after this returns — only the copied fields travel.
        /// </summary>
        public static IncomingLiveAnnouncePayload TryCreate(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isFromMe,
            bool suppressToast,
            bool quietForSync,
            int totalUnread,
            bool isMuted,
            string avatarUrl,
            int chatUnread,
            bool isCommunityAnnounce = false)
        {
            if (!IncomingLiveNotifyGate.ShouldAnnounce(isFromMe) ||
                string.IsNullOrWhiteSpace(chatJid))
            {
                return null;
            }

            return new IncomingLiveAnnouncePayload
            {
                ChatJid = chatJid.Trim(),
                ChatName = chatName,
                SenderName = senderName,
                Preview = preview,
                IsGroup = isGroup,
                IsMuted = isMuted,
                SuppressToast = suppressToast || quietForSync,
                TotalUnread = totalUnread,
                AvatarUrl = avatarUrl,
                ChatUnread = chatUnread,
                IsCommunityAnnounce = isCommunityAnnounce
            };
        }

        /// <summary>Reads mute / avatar / unread from the row after the UI strip apply.</summary>
        public static IncomingLiveAnnouncePayload TryCreateFromChat(
            string chatJid,
            string chatName,
            string senderName,
            string preview,
            bool isGroup,
            bool isFromMe,
            bool suppressToast,
            bool quietForSync,
            int totalUnread,
            ChatItem chat,
            bool mutedFallback)
        {
            bool isMuted = chat != null ? chat.IsMutedLocally : mutedFallback;
            bool isAnnounce = chat != null && chat.IsCommunityAnnounce;
            string avatar = chat != null ? chat.GetAvatarUrl(preferHigh: false) : null;
            int chatUnread = chat != null ? System.Math.Max(0, chat.UnreadCount) : -1;
            string name = chatName;
            if (string.IsNullOrWhiteSpace(name) && chat != null)
            {
                name = chat.Name;
            }

            return TryCreate(
                chatJid,
                name,
                senderName,
                preview,
                isGroup,
                isFromMe,
                suppressToast,
                quietForSync,
                totalUnread,
                isMuted,
                avatar,
                chatUnread,
                isAnnounce);
        }
    }
}
