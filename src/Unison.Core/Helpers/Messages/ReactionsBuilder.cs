using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Builds reaction display payloads for UI (emoji line / chips). No ResourceLoader.
    /// </summary>
    public static class ReactionsBuilder
    {
        /// <summary>
        /// Single-button label: distinct emojis (first-seen order) + total reactors.
        /// Example: "👍 ❤️ 😂 3".
        /// </summary>
        public static string BuildEmojiLine(IEnumerable<MessageReaction> reactions)
        {
            if (reactions == null)
            {
                return string.Empty;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            int total = 0;

            foreach (var reaction in reactions)
            {
                if (reaction == null || string.IsNullOrWhiteSpace(reaction.Emoji))
                {
                    continue;
                }

                total++;
                string emoji = reaction.Emoji.Trim();
                if (!seen.Add(emoji))
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(emoji);
            }

            if (total == 0)
            {
                return string.Empty;
            }

            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(total);
            return sb.ToString();
        }

        /// <summary>
        /// Rounded-chip source: one entry per distinct emoji with tally.
        /// </summary>
        public static IList<ReactionChip> BuildChips(IEnumerable<MessageReaction> reactions)
        {
            var result = new List<ReactionChip>();
            if (reactions == null)
            {
                return result;
            }

            foreach (var group in reactions
                .Where(r => r != null && !string.IsNullOrWhiteSpace(r.Emoji))
                .GroupBy(r => r.Emoji.Trim(), StringComparer.Ordinal))
            {
                result.Add(new ReactionChip
                {
                    Emoji = group.Key,
                    Count = group.Count()
                });
            }

            return result;
        }
    }
}
