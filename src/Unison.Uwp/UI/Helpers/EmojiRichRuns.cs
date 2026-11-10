using System.Collections.Generic;
using Unison.Core.Helpers;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Appends plain text as mixed <see cref="Run"/>s: body on the ambient font,
    /// emoji graphemes on <c>EmojiFont</c> (WhatsApp Color Emoji).
    /// </summary>
    internal static class EmojiRichRuns
    {
        private static FontFamily _emojiFont;
        private static bool _emojiFontResolved;

        /// <summary>
        /// Replaces <paramref name="block"/> content with mixed body/emoji runs.
        /// Empty text clears the block (same as setting Text to empty).
        /// </summary>
        public static void SetTextBlock(TextBlock block, string text)
        {
            if (block == null)
            {
                return;
            }

            block.Text = string.Empty;
            block.Inlines.Clear();
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            // Virtualised rows realise this per item. A name with no emoji cannot need
            // mixed runs, so paint it flat and skip the grapheme walk entirely.
            if (!EmojiText.MayContainEmoji(text))
            {
                block.Text = text;
                return;
            }

            AppendInlines(block.Inlines, text);
        }

        public static void Append(
            Paragraph paragraph,
            string text,
            FontWeight? weight = null,
            Brush foreground = null)
        {
            if (paragraph == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            AppendInlines(paragraph.Inlines, text, weight, foreground);
        }

        private static void AppendInlines(
            InlineCollection inlines,
            string text,
            FontWeight? weight = null,
            Brush foreground = null)
        {
            if (!EmojiText.MayContainEmoji(text))
            {
                inlines.Add(BuildRun(text, weight, foreground, false, null));
                return;
            }

            FontFamily emojiFont = ResolveEmojiFont();
            IReadOnlyList<EmojiText.Segment> segments = EmojiText.Split(text);
            for (int i = 0; i < segments.Count; i++)
            {
                EmojiText.Segment segment = segments[i];
                if (string.IsNullOrEmpty(segment.Text))
                {
                    continue;
                }

                inlines.Add(BuildRun(segment.Text, weight, foreground, segment.IsEmoji, emojiFont));
            }
        }

        private static Run BuildRun(
            string text,
            FontWeight? weight,
            Brush foreground,
            bool isEmoji,
            FontFamily emojiFont)
        {
            var run = new Run { Text = text };
            if (weight.HasValue)
            {
                run.FontWeight = weight.Value;
            }

            if (foreground != null)
            {
                run.Foreground = foreground;
            }

            if (isEmoji && emojiFont != null)
            {
                run.FontFamily = emojiFont;
            }

            return run;
        }

        public static void AppendToHyperlink(
            Hyperlink hyperlink,
            string text,
            FontFamily emojiFont = null)
        {
            if (hyperlink == null || string.IsNullOrEmpty(text))
            {
                return;
            }

            if (!EmojiText.MayContainEmoji(text))
            {
                hyperlink.Inlines.Add(new Run { Text = text });
                return;
            }

            FontFamily font = emojiFont ?? ResolveEmojiFont();
            IReadOnlyList<EmojiText.Segment> segments = EmojiText.Split(text);
            for (int i = 0; i < segments.Count; i++)
            {
                EmojiText.Segment segment = segments[i];
                if (string.IsNullOrEmpty(segment.Text))
                {
                    continue;
                }

                var run = new Run { Text = segment.Text };
                if (segment.IsEmoji && font != null)
                {
                    run.FontFamily = font;
                }

                hyperlink.Inlines.Add(run);
            }
        }

        private static FontFamily ResolveEmojiFont()
        {
            if (_emojiFontResolved)
            {
                return _emojiFont;
            }

            _emojiFontResolved = true;
            try
            {
                _emojiFont = Application.Current.Resources["EmojiFont"] as FontFamily;
            }
            catch
            {
                _emojiFont = null;
            }

            return _emojiFont;
        }
    }
}
