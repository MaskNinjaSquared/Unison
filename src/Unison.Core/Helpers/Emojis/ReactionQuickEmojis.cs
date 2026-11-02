// =============================================================================
// ReactionQuickEmojis
//
// Fixed strip of common reaction glyphs shown above the bubble context menu.
// ForStrip puts the caller's current reaction at index 0 (selected): move within
// the twelve, or prepend a thirteenth when it came from the full picker.
// =============================================================================
using System;
using System.Collections.Generic;

namespace Unison.Core.Helpers
{
    public static class ReactionQuickEmojis
    {
        public const int Count = 12;

        /// <summary>WhatsApp-style common reactions (exactly <see cref="Count"/>).</summary>
        public static IReadOnlyList<string> All { get; } = new[]
        {
            "👍",
            "❤️",
            "😂",
            "😮",
            "😢",
            "🙏",
            "👏",
            "🔥",
            "🎉",
            "🤩",
            "💩",
            "🤮"
        };

        /// <summary>
        /// Glyphs for the context-menu quick bar. Blank <paramref name="myEmoji"/> keeps
        /// <see cref="All"/>. A match among the defaults moves to the front (still twelve).
        /// Anything else is prepended (thirteen).
        /// </summary>
        public static IReadOnlyList<string> ForStrip(string myEmoji)
        {
            if (string.IsNullOrWhiteSpace(myEmoji))
            {
                return All;
            }

            string mine = myEmoji.Trim();
            var ordered = new List<string>(Count + 1) { mine };
            for (int i = 0; i < All.Count; i++)
            {
                string glyph = All[i];
                if (string.Equals(glyph, mine, StringComparison.Ordinal))
                {
                    continue;
                }

                ordered.Add(glyph);
            }

            return ordered;
        }
    }
}
