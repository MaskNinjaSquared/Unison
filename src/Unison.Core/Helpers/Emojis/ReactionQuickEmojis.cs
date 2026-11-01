// =============================================================================
// ReactionQuickEmojis
//
// Fixed strip of common reaction glyphs shown above the bubble context menu.
// =============================================================================
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
    }
}
