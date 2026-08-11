using System;
using Unison.Core.Helpers;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Imgur-inspired comment rich helper: binds plain text onto a
    /// <see cref="RichTextBlock"/> and turns detected URLs into underlined Hyperlinks.
    /// </summary>
    public static class CommentRichService
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(CommentRichService),
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

            Brush linkBrush = block.Foreground;
            try
            {
                // Prefer theme hyperlink accent when available.
                var accent = Application.Current.Resources["SystemControlHyperlinkTextBrush"] as Brush;
                if (accent != null)
                {
                    linkBrush = accent;
                }
            }
            catch
            {
            }

            var segments = MessageLinkParser.Parse(text);
            for (int i = 0; i < segments.Count; i++)
            {
                MessageLinkParser.Segment segment = segments[i];
                if (segment.IsLink)
                {
                    Uri uri;
                    if (!Uri.TryCreate(segment.NavigateUrl, UriKind.Absolute, out uri))
                    {
                        AppendPlainWithBreaks(paragraph, segment.Text);
                        continue;
                    }

                    var hyperlink = new Hyperlink
                    {
                        NavigateUri = uri,
                        UnderlineStyle = UnderlineStyle.Single,
                        Foreground = linkBrush
                    };
                    hyperlink.Inlines.Add(new Run { Text = segment.Text });
                    paragraph.Inlines.Add(hyperlink);
                }
                else
                {
                    AppendPlainWithBreaks(paragraph, segment.Text);
                }
            }

            block.Blocks.Add(paragraph);
        }

        private static void AppendPlainWithBreaks(Paragraph paragraph, string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            int start = 0;
            for (int i = 0; i < normalized.Length; i++)
            {
                if (normalized[i] != '\n')
                {
                    continue;
                }

                if (i > start)
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = normalized.Substring(start, i - start)
                    });
                }

                paragraph.Inlines.Add(new LineBreak());
                start = i + 1;
            }

            if (start < normalized.Length)
            {
                paragraph.Inlines.Add(new Run
                {
                    Text = normalized.Substring(start)
                });
            }
        }
    }
}
