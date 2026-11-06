// =============================================================================
// NewChatListSeed
//
// Builds the catalogue row for a chat that just became known locally (new 1:1
// or a group create answer) before any message tip exists. Stamping UtcNow keeps
// SortChatsForDisplay from burying the row under DateTime.MinValue.
// =============================================================================
using System;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class NewChatListSeed
    {
        public static ChatItem Build(
            string jid,
            string displayName,
            ChatKind kind,
            DateTime utcNow,
            string yesterdayLabel)
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("A chat JID is required", nameof(jid));
            }

            string name = string.IsNullOrWhiteSpace(displayName) ? jid : displayName.Trim();
            return new ChatItem
            {
                JID = jid,
                Name = name,
                Kind = kind,
                Status = ChatStatus.Active,
                LastMessage = string.Empty,
                LastMessageTimestampUtc = utcNow,
                Timestamp = WhatsAppMapper.FormatTimestamp(utcNow, yesterdayLabel)
            };
        }

        /// <summary>
        /// Community create answer: the parent JID must project as a hub, not a plain group,
        /// until the listing brings its linked subgroups.
        /// </summary>
        public static ChatItem BuildCommunityHub(
            string jid,
            string displayName,
            ChatKind kind,
            DateTime utcNow,
            string yesterdayLabel)
        {
            ChatItem hub = Build(jid, displayName, kind, utcNow, yesterdayLabel);
            hub.IsCommunityParent = true;
            return hub;
        }
    }
}
