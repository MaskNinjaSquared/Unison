using System.Collections.Generic;

namespace Unison.Core.Models
{
    /// <summary>
    /// One sticker-box emoji section (Recents, Smileys, …).
    /// </summary>
    public sealed class EmojiCategory
    {
        public const double SegoeJumpFontSize = 24.0;
        public const double UnisonJumpFontSize = 30.0;

        public EmojiCategory(
            string id,
            string title,
            string jumpGlyph,
            bool jumpUsesUnisonFont,
            IReadOnlyList<string> glyphs)
        {
            Id = id;
            Title = title;
            JumpGlyph = jumpGlyph ?? string.Empty;
            JumpUsesUnisonFont = jumpUsesUnisonFont;
            JumpFontSize = jumpUsesUnisonFont ? UnisonJumpFontSize : SegoeJumpFontSize;
            Glyphs = glyphs ?? new string[0];
        }

        public string Id { get; }

        public string Title { get; }

        /// <summary>
        /// SemanticZoom jump-tile glyph (Segoe Fluent / MDL2 or UnisonIcons PUA).
        /// </summary>
        public string JumpGlyph { get; }

        /// <summary>
        /// True when <see cref="JumpGlyph"/> is from UnisonIcons (sports / nature / food / symbols).
        /// </summary>
        public bool JumpUsesUnisonFont { get; }

        /// <summary>
        /// Jump-tile <c>FontSize</c> — Unison glyphs need a bit more optical size than Segoe.
        /// </summary>
        public double JumpFontSize { get; }

        public IReadOnlyList<string> Glyphs { get; }
    }
}
