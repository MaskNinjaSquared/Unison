// =============================================================================
// LiveIncomingStripCoalesce
//
// Under a live/offline flood the pump fires one strip hop per message. On
// frugal ARM those hops serialize on the urgent UI dispatcher and the same
// chat (busy group) waits tens of seconds behind its own earlier tips. Merge
// pending requests per chat so one hop paints the newest tip and the summed
// unread delta.
// =============================================================================
using System;
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class LiveIncomingStripCoalesce
    {
        /// <summary>
        /// Folds <paramref name="incoming"/> into <paramref name="target"/>: unread always
        /// accumulates; tip fields follow the newer (or equal) timestamp.
        /// </summary>
        public static void Merge(
            LiveIncomingChatListApplyRequest target,
            LiveIncomingChatListApplyRequest incoming)
        {
            if (target == null || incoming == null)
            {
                return;
            }

            int targetUnread = EffectiveUnread(target);
            int incomingUnread = EffectiveUnread(incoming);
            target.UnreadDelta = targetUnread + incomingUnread;
            target.CountsAsUnread = target.UnreadDelta > 0;
            if (incoming.HasCitation)
            {
                target.HasCitation = true;
            }

            if (incoming.Timestamp >= target.Timestamp)
            {
                target.PreviewText = incoming.PreviewText;
                target.Timestamp = incoming.Timestamp;
                target.PreviewKind = incoming.PreviewKind;
                target.AuthorPrefix = incoming.AuthorPrefix;
                target.MentionedJids = incoming.MentionedJids;
                target.IsFromMe = incoming.IsFromMe;
                target.SendState = incoming.SendState;
                target.MessageId = incoming.MessageId;
                target.ParticipantJid = incoming.ParticipantJid;
                target.SenderName = incoming.SenderName;
                target.IsGroup = incoming.IsGroup;
            }

            if (string.IsNullOrWhiteSpace(target.AliasLid) &&
                !string.IsNullOrWhiteSpace(incoming.AliasLid))
            {
                target.AliasLid = incoming.AliasLid;
            }

            if (string.IsNullOrWhiteSpace(target.AliasPn) &&
                !string.IsNullOrWhiteSpace(incoming.AliasPn))
            {
                target.AliasPn = incoming.AliasPn;
            }
        }

        /// <summary>
        /// Live legacy path maps CountsAsUnread onto a delta of 1 when UnreadDelta is 0.
        /// </summary>
        public static int EffectiveUnread(LiveIncomingChatListApplyRequest request)
        {
            if (request == null)
            {
                return 0;
            }

            if (request.UnreadDelta != 0)
            {
                return request.UnreadDelta;
            }

            return request.CountsAsUnread ? 1 : 0;
        }
    }
}
