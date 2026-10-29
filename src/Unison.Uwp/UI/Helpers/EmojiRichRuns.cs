using System.Collections.Generic;
using Unison.Core.Helpers;
using Windows.UI.Text;
using Windows.UI.Xaml;
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

            FontFamily emojiFont = ResolveEmojiFont();
            IReadOnlyList<EmojiText.Segment> segments = EmojiText.Split(text);
            for (int i = 0; i < segments.Count; i++)
            {
                EmojiText.Segment segment = segments[i];
                if (string.IsNullOrEmpty(segment.Text))
                {
                    continue;
                }

                var run = new Run { Text = segment.Text };
                if (weight.HasValue)
                {
                    run.FontWeight = weight.Value;
                }

                if (foreground != null)
                {
                    run.Foreground = foreground;
                }

                if (segment.IsEmoji && emojiFont != null)
                {
                    run.FontFamily = emojiFont;
                }

                paragraph.Inlines.Add(run);
            }
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
