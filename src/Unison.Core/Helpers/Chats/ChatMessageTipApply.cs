using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts one message onto a chat row's list strip when the tip is allowed.
    /// </summary>
    /// <remarks>
    /// Combines <see cref="ChatListStrip.FromMessage"/> with
    /// <see cref="LiveChatPreviewApplier.ApplyIfNewer"/>. The client used to own this glue as
    /// ApplyMessageAsChatTip; the pieces were already in Core.
    /// </remarks>
    public static class ChatMessageTipApply
    {
        /// <returns>True when the row's strip changed.</returns>
        public static bool Apply(
            ChatItem chat,
            ChatMessage message,
            bool isGroup,
            bool force,
            string selfDisplayName,
            string canonicalParticipantJid,
            string yesterdayLabel)
        {
            ChatListStrip strip = ChatListStrip.FromMessage(
                message,
                isGroup,
                selfDisplayName,
                canonicalParticipantJid);
            if (strip == null)
            {
                return false;
            }

            return LiveChatPreviewApplier.ApplyIfNewer(
                chat,
                strip.Preview,
                strip.Timestamp,
                force,
                strip.Kind,
                strip.AuthorPrefix,
                strip.MentionedJids,
                strip.IsFromMe,
                strip.SendState,
                strip.MessageId,
                yesterdayLabel,
                strip.ParticipantJid,
                strip.SenderName);
        }
    }
}
