using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Windows.UI;
using Windows.UI.Text;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// Rich bubbles: URLs as hyperlinks + @digits mentions resolved to contact names.
    /// </summary>
    public static class CommentRichService
    {
        private static readonly Regex MentionRegex = new Regex(
            @"@(\d{5,20})\b",
            RegexOptions.Compiled);

        private static readonly SolidColorBrush DefaultMentionBrush =
            new SolidColorBrush(Color.FromArgb(0xFF, 0x25, 0xD3, 0x66));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(CommentRichService),
                new PropertyMetadata(null, OnTextOrMentionsChanged));

        public static readonly DependencyProperty MentionedJidsProperty =
            DependencyProperty.RegisterAttached(
                "MentionedJids",
                typeof(object),
                typeof(CommentRichService),
                new PropertyMetadata(null, OnTextOrMentionsChanged));

        /// <summary>
        /// Theme resource key for URL/mention link color (e.g. ChatDetailSentLinkBrush).
        /// </summary>
        public static readonly DependencyProperty LinkBrushKeyProperty =
            DependencyProperty.RegisterAttached(
                "LinkBrushKey",
                typeof(string),
                typeof(CommentRichService),
                new PropertyMetadata(null, OnTextOrMentionsChanged));

        public static void SetText(DependencyObject element, string value)
        {
            element.SetValue(TextProperty, value);
        }

        public static string GetText(DependencyObject element)
        {
            return (string)element.GetValue(TextProperty);
        }

        public static void SetMentionedJids(DependencyObject element, object value)
        {
            element.SetValue(MentionedJidsProperty, value);
        }

        public static object GetMentionedJids(DependencyObject element)
        {
            return element.GetValue(MentionedJidsProperty);
        }

        public static void SetLinkBrushKey(DependencyObject element, string value)
        {
            element.SetValue(LinkBrushKeyProperty, value);
        }

        public static string GetLinkBrushKey(DependencyObject element)
        {
            return (string)element.GetValue(LinkBrushKeyProperty);
        }

        private static void OnTextOrMentionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var block = d as RichTextBlock;
            if (block == null)
            {
                return;
            }

            Apply(block, GetText(block), GetMentionedJids(block) as IEnumerable<string>);
        }

        /// <summary>
        /// Resolves @digits mentions to display names for plain TextBlock previews
        /// (chat list strip). Does not build rich runs — layout stays reliable in list rows.
        /// </summary>
        public static string FormatMentionsPlain(string text, IEnumerable<string> mentionedJids = null)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            IWhatsAppService whatsApp = null;
            try
            {
                whatsApp = App.GetWhatsAppService();
            }
            catch
            {
            }

            var mentionLookup = BuildMentionLookup(mentionedJids, whatsApp);
            if ((mentionLookup == null || mentionLookup.Count == 0) && whatsApp == null)
            {
                return text;
            }

            MatchCollection matches = MentionRegex.Matches(text);
            if (matches.Count == 0)
            {
                return text;
            }

            var sb = new System.Text.StringBuilder(text.Length + 16);
            int index = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                Match m = matches[i];
                if (m.Index > index)
                {
                    sb.Append(text, index, m.Index - index);
                }

                string digits = m.Groups[1].Value;
                string displayName = null;
                if (mentionLookup != null)
                {
                    mentionLookup.TryGetValue(digits, out displayName);
                }

                if (string.IsNullOrWhiteSpace(displayName) && whatsApp != null)
                {
                    displayName = SafeResolveName(whatsApp, digits + "@s.whatsapp.net");
                }

                if (!string.IsNullOrWhiteSpace(displayName) &&
                    displayName.IndexOf('@') < 0 &&
                    !string.Equals(displayName, digits, StringComparison.Ordinal))
                {
                    sb.Append('@');
                    sb.Append(displayName);
                }
                else
                {
                    sb.Append(m.Value);
                }

                index = m.Index + m.Length;
            }

            if (index < text.Length)
            {
                sb.Append(text, index, text.Length - index);
            }

            return sb.ToString();
        }

        public static void Apply(RichTextBlock block, string text, IEnumerable<string> mentionedJids = null)
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
            Brush mentionBrush = ResolveMentionBrush(block);

            IWhatsAppService whatsApp = null;
            try
            {
                whatsApp = App.GetWhatsAppService();
            }
            catch
            {
            }

            var mentionLookup = BuildMentionLookup(mentionedJids, whatsApp);
            var segments = MessageLinkParser.Parse(text);
            for (int i = 0; i < segments.Count; i++)
            {
                MessageLinkParser.Segment segment = segments[i];
                if (segment.IsLink)
                {
                    Uri uri;
                    if (!Uri.TryCreate(segment.NavigateUrl, UriKind.Absolute, out uri))
                    {
                        AppendPlainWithMentions(paragraph, segment.Text, mentionLookup, whatsApp, mentionBrush);
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
                    AppendPlainWithMentions(paragraph, segment.Text, mentionLookup, whatsApp, mentionBrush);
                }
            }

            block.Blocks.Add(paragraph);
        }

        private static Brush ResolveThemeBrush(string key, Brush fallback)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return fallback;
            }

            try
            {
                var themed = Application.Current.Resources[key] as Brush;
                if (themed != null)
                {
                    return themed;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static Brush ResolveLinkBrush(RichTextBlock block)
        {
            Brush fallback = block.Foreground;
            try
            {
                var accent = Application.Current.Resources["SystemControlHyperlinkTextBrush"] as Brush;
                if (accent != null)
                {
                    fallback = accent;
                }
            }
            catch
            {
            }

            return ResolveThemeBrush(GetLinkBrushKey(block), fallback);
        }

        private static Brush ResolveMentionBrush(RichTextBlock block)
        {
            string linkKey = GetLinkBrushKey(block);
            string mentionKey = null;
            if (!string.IsNullOrWhiteSpace(linkKey) &&
                linkKey.EndsWith("LinkBrush", StringComparison.Ordinal))
            {
                mentionKey = linkKey.Substring(0, linkKey.Length - "LinkBrush".Length) + "MentionBrush";
            }

            return ResolveThemeBrush(mentionKey, DefaultMentionBrush);
        }

        private static Dictionary<string, string> BuildMentionLookup(
            IEnumerable<string> mentionedJids,
            IWhatsAppService whatsApp)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            if (mentionedJids == null || whatsApp == null)
            {
                return map;
            }

            foreach (string jid in mentionedJids)
            {
                if (string.IsNullOrWhiteSpace(jid))
                {
                    continue;
                }

                string digits = ExtractUserDigits(jid);
                if (string.IsNullOrEmpty(digits) || map.ContainsKey(digits))
                {
                    continue;
                }

                string name = SafeResolveName(whatsApp, jid);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    map[digits] = name;
                }
            }

            return map;
        }

        private static void AppendPlainWithMentions(
            Paragraph paragraph,
            string text,
            Dictionary<string, string> mentionLookup,
            IWhatsAppService whatsApp,
            Brush mentionBrush)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            Brush brush = mentionBrush ?? DefaultMentionBrush;
            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            MatchCollection matches = MentionRegex.Matches(normalized);
            int index = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                Match m = matches[i];
                if (m.Index > index)
                {
                    AppendPlainWithBreaks(paragraph, normalized.Substring(index, m.Index - index));
                }

                string digits = m.Groups[1].Value;
                string displayName = null;
                if (mentionLookup != null)
                {
                    mentionLookup.TryGetValue(digits, out displayName);
                }

                if (string.IsNullOrWhiteSpace(displayName) && whatsApp != null)
                {
                    displayName = SafeResolveName(whatsApp, digits + "@s.whatsapp.net");
                }

                if (!string.IsNullOrWhiteSpace(displayName) &&
                    displayName.IndexOf('@') < 0 &&
                    !string.Equals(displayName, digits, StringComparison.Ordinal))
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = "@" + displayName,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = brush
                    });
                }
                else
                {
                    AppendPlainWithBreaks(paragraph, m.Value);
                }

                index = m.Index + m.Length;
            }

            if (index < normalized.Length)
            {
                AppendPlainWithBreaks(paragraph, normalized.Substring(index));
            }
        }

        private static string SafeResolveName(IWhatsAppService whatsApp, string jid)
        {
            try
            {
                return whatsApp.ResolveDisplayName(jid, "mention");
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractUserDigits(string jid)
        {
            if (string.IsNullOrEmpty(jid))
            {
                return null;
            }

            int at = jid.IndexOf('@');
            string user = at > 0 ? jid.Substring(0, at) : jid;
            int colon = user.IndexOf(':');
            if (colon > 0)
            {
                user = user.Substring(0, colon);
            }

            // Keep only leading digits for PN-style JIDs.
            int end = 0;
            while (end < user.Length && char.IsDigit(user[end]))
            {
                end++;
            }

            return end > 0 ? user.Substring(0, end) : null;
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
