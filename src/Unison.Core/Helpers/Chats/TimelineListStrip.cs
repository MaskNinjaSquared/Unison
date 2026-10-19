// =============================================================================
// TimelineListStrip
//
// When a chat opens, its list row is corrected from the timeline the user is
// looking at. Two rules: which message is the tip, and whether the row should
// take it.
//
// The tip is the newest message by clock (then id), not the last bubble: arrival
// and store order can be shuffled, and bubble runs re-order for display.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Mappers;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class TimelineListStrip
    {
        /// <summary>The newest dated message; on the same clock the higher id wins.</summary>
        public static ChatMessage PickTip(IReadOnlyList<ChatMessage> timeline)
        {
            if (timeline == null)
            {
                return null;
            }

            ChatMessage tip = null;
            DateTime tipUtc = DateTime.MinValue;
            for (int i = 0; i < timeline.Count; i++)
            {
                ChatMessage candidate = timeline[i];
                if (candidate == null || candidate.Timestamp == DateTime.MinValue)
                {
                    continue;
                }

                DateTime candidateUtc = WhatsAppMapper.ToUtc(candidate.Timestamp);
                if (tip == null ||
                    candidateUtc > tipUtc ||
                    (candidateUtc == tipUtc &&
                     string.CompareOrdinal(candidate.Id ?? string.Empty, tip.Id ?? string.Empty) > 0))
                {
                    tip = candidate;
                    tipUtc = candidateUtc;
                }
            }

            return tip;
        }

        /// <summary>
        /// Whether the row should show <paramref name="tip"/> rendered as given.
        /// </summary>
        /// <remarks>
        /// Only advance by clock, or refresh when the rendering differs at the same second. A
        /// different id is trusted even against a newer strip clock: that clock may have been
        /// poisoned (Unspecified read as local, +3h in Brazil).
        /// </remarks>
        public static bool ShouldReplace(
            ChatItem chat,
            ChatMessage tip,
            string preview,
            ChatPreviewKind previewKind,
            string authorPrefix,
            MessageSendState sendState)
        {
            if (chat == null || tip == null)
            {
                return false;
            }

            DateTime tipUtc = WhatsAppMapper.ToUtc(tip.Timestamp);
            DateTime currentUtc = chat.LastMessageTimestampUtc.HasValue
                ? WhatsAppMapper.ToUtc(chat.LastMessageTimestampUtc.Value)
                : DateTime.MinValue;

            bool tipIdDiffers = !string.IsNullOrWhiteSpace(tip.Id) &&
                                !string.Equals(chat.LastMessageId, tip.Id, StringComparison.Ordinal);

            return tipIdDiffers ||
                   tipUtc > currentUtc ||
                   (tipUtc == currentUtc &&
                    currentUtc != DateTime.MinValue &&
                    (!string.Equals(chat.LastMessage, preview, StringComparison.Ordinal) ||
                     chat.LastMessageKind != previewKind ||
                     !string.Equals(chat.LastMessageAuthor, authorPrefix, StringComparison.Ordinal) ||
                     chat.LastMessageIsFromMe != tip.IsFromMe ||
                     chat.LastMessageSendState != sendState)) ||
                   (currentUtc == DateTime.MinValue && tipUtc != DateTime.MinValue);
        }
    }
}
