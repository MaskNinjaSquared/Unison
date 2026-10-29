// =============================================================================
// EmojiText
//
// Splits plain strings into coalesced plain / emoji grapheme runs so UWP rich
// bubbles can paint emoji with WhatsApp Color Emoji while body text stays on
// MainFont. Detection is catalog + Unicode emoji ranges (incoming messages are
// not limited to the sticker-box list).
// =============================================================================
using System.Collections.Generic;
using System.Globalization;

namespace Unison.Core.Helpers
{
    public static class EmojiText
    {
        public readonly struct Segment
        {
            public Segment(string text, bool isEmoji)
            {
                Text = text;
                IsEmoji = isEmoji;
            }

            public string Text { get; }

            public bool IsEmoji { get; }
        }

        /// <summary>
        /// Coalesced plain/emoji slices over <paramref name="text"/> (grapheme clusters).
        /// Empty input yields no segments.
        /// </summary>
        public static IReadOnlyList<Segment> Split(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return new Segment[0];
            }

            var result = new List<Segment>();
            var buffer = new System.Text.StringBuilder();
            bool? bufferIsEmoji = null;

            TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
            while (elements.MoveNext())
            {
                string element = elements.GetTextElement();
                if (string.IsNullOrEmpty(element))
                {
                    continue;
                }

                bool emoji = IsEmojiElement(element);
                if (bufferIsEmoji.HasValue && bufferIsEmoji.Value != emoji && buffer.Length > 0)
                {
                    result.Add(new Segment(buffer.ToString(), bufferIsEmoji.Value));
                    buffer.Clear();
                }

                bufferIsEmoji = emoji;
                buffer.Append(element);
            }

            if (buffer.Length > 0 && bufferIsEmoji.HasValue)
            {
                result.Add(new Segment(buffer.ToString(), bufferIsEmoji.Value));
            }

            return result;
        }

        /// <summary>True when a single text element should use the emoji color font.</summary>
        public static bool IsEmojiElement(string element)
        {
            if (string.IsNullOrEmpty(element))
            {
                return false;
            }

            if (EmojiCatalog.IsKnownGlyph(element))
            {
                return true;
            }

            int i = 0;
            while (i < element.Length)
            {
                int code;
                if (char.IsHighSurrogate(element[i]) &&
                    i + 1 < element.Length &&
                    char.IsLowSurrogate(element[i + 1]))
                {
                    code = char.ConvertToUtf32(element, i);
                    i += 2;
                }
                else
                {
                    code = element[i];
                    i++;
                }

                if (IsEmojiCodePoint(code))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsEmojiCodePoint(int code)
        {
            // VS16 / ZWJ / combining keycap / skin tones → emoji sequence glue.
            if (code == 0xFE0F || code == 0x200D || code == 0x20E3)
            {
                return true;
            }

            if (code >= 0x1F3FB && code <= 0x1F3FF)
            {
                return true;
            }

            // Regional indicator symbols (flags).
            if (code >= 0x1F1E6 && code <= 0x1F1FF)
            {
                return true;
            }

            // Misc symbols + dingbats often used as emoji.
            if ((code >= 0x2600 && code <= 0x27BF) ||
                (code >= 0x2300 && code <= 0x23FF) ||
                (code >= 0x2B50 && code <= 0x2B55))
            {
                return true;
            }

            // Emoticons / pictographs / transport / supplemental / ext-A.
            if ((code >= 0x1F300 && code <= 0x1F5FF) ||
                (code >= 0x1F600 && code <= 0x1F64F) ||
                (code >= 0x1F680 && code <= 0x1F6FF) ||
                (code >= 0x1F700 && code <= 0x1F77F) ||
                (code >= 0x1F780 && code <= 0x1F7FF) ||
                (code >= 0x1F800 && code <= 0x1F8FF) ||
                (code >= 0x1F900 && code <= 0x1F9FF) ||
                (code >= 0x1FA00 && code <= 0x1FAFF) ||
                (code >= 0x1FC00 && code <= 0x1FFFD))
            {
                return true;
            }

            return false;
        }
    }
}
