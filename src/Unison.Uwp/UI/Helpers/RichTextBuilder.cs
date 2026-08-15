using System;
using System.Text.RegularExpressions;
using Unison.Core.Helpers;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Builds <see cref="RichTextBlock"/> content from plain text: URLs and @handles
    /// become underlined hyperlinks (handles → https://github.com/…). Newlines become line breaks.
    /// </summary>
    public static class RichTextBuilder
    {
        private static readonly Regex MentionRegex = new Regex(
            @"@[A-Za-z0-9](?:[A-Za-z0-9]|-(?=[A-Za-z0-9])){0,38}",
            RegexOptions.Compiled);


        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(RichTextBuilder),
                new PropertyMetadata(null, OnTextChanged));

        public static void SetText(DependencyObject element, string value)
        {
            element.SetValue(TextProperty, value);
        }

        public static string GetText(DependencyObject element)
        {
            return (string)element.GetValue(TextProperty);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var block = d as RichTextBlock;
            if (block == null)
            {
                return;
            }

            Apply(block, e.NewValue as string);
        }

        public static void Apply(RichTextBlock block, string text)
        {
            if (block == null)
            {
                return;
            }

            block.Blocks.Clear();
            var paragraph = new Paragraph();

            if (string.IsNullOrEmpty(text))
            {
                block.Blocks.Add(paragraph);
                return;
            }

            Brush linkBrush = ResolveLinkBrush(block);

            var urlSegments = MessageLinkParser.Parse(text);
            for (int i = 0; i < urlSegments.Count; i++)
            {
                MessageLinkParser.Segment segment = urlSegments[i];
                if (segment.IsLink)
                {
                    Uri uri;
                    if (!Uri.TryCreate(segment.NavigateUrl, UriKind.Absolute, out uri))
                    {
                        AppendPlainMentionsAndBreaks(paragraph, segment.Text, linkBrush);
                        continue;
                    }

                    paragraph.Inlines.Add(CreateHyperlink(segment.Text, uri, linkBrush));
                }
                else
                {
                    AppendPlainMentionsAndBreaks(paragraph, segment.Text, linkBrush);
                }
            }

            block.Blocks.Add(paragraph);
        }

        private static Brush ResolveLinkBrush(RichTextBlock block)
        {
            Brush linkBrush = block.Foreground;
            try
            {
                var accent = Application.Current.Resources["SystemControlHyperlinkTextBrush"] as Brush;
                if (accent != null)
                {
                    linkBrush = accent;
                }
            }
            catch
            {
            }

            return linkBrush;
        }

        private static void AppendPlainMentionsAndBreaks(
            Paragraph paragraph,
            string text,
            Brush linkBrush)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int lineStart = 0;
            for (int i = 0; i <= normalized.Length; i++)
            {
                bool atEnd = i == normalized.Length;
                bool atBreak = !atEnd && normalized[i] == '\n';
                if (!atEnd && !atBreak)
                {
                    continue;
                }

                if (i > lineStart)
                {
                    AppendMentions(paragraph, normalized.Substring(lineStart, i - lineStart), linkBrush);
                }

                if (atBreak)
                {
                    paragraph.Inlines.Add(new LineBreak());
                }

                lineStart = i + 1;
            }
        }

        private static void AppendMentions(Paragraph paragraph, string text, Brush linkBrush)
        {
            MatchCollection matches = MentionRegex.Matches(text);
            int index = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                Match m = matches[i];
                if (m.Index > index)
                {
                    paragraph.Inlines.Add(new Run { Text = text.Substring(index, m.Index - index) });
                }

                string handle = m.Value;
                string navigate = "https://github.com/" + handle.Substring(1);
                Uri uri;
                if (Uri.TryCreate(navigate, UriKind.Absolute, out uri))
                {
                    paragraph.Inlines.Add(CreateHyperlink(handle, uri, linkBrush));
                }
                else
                {
                    paragraph.Inlines.Add(new Run { Text = handle });
                }

                index = m.Index + m.Length;
            }

            if (index < text.Length)
            {
                paragraph.Inlines.Add(new Run { Text = text.Substring(index) });
            }
        }

        private static Hyperlink CreateHyperlink(string display, Uri uri, Brush linkBrush)
        {
            var hyperlink = new Hyperlink
            {
                NavigateUri = uri,
                UnderlineStyle = UnderlineStyle.Single,
                Foreground = linkBrush
            };
            hyperlink.Inlines.Add(new Run { Text = display });
            return hyperlink;
        }
    }
}
