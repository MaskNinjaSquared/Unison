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
            @"@((?:\d{5,24})(?:@(?:s\.whatsapp\.net|lid|hosted\.lid))?)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

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

        public static readonly DependencyProperty MentionLookupProperty =
            DependencyProperty.RegisterAttached(
                "MentionLookup",
                typeof(object),
                typeof(CommentRichService),
                new PropertyMetadata(null, OnTextOrMentionsChanged));

        public static readonly DependencyProperty RefreshKeyProperty =
            DependencyProperty.RegisterAttached(
                "RefreshKey",
                typeof(int),
                typeof(CommentRichService),
                new PropertyMetadata(0, OnTextOrMentionsChanged));

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

        public static void SetMentionLookup(DependencyObject element, object value)
        {
            element.SetValue(MentionLookupProperty, value);
        }

        public static object GetMentionLookup(DependencyObject element)
        {
            return element.GetValue(MentionLookupProperty);
        }

        public static void SetRefreshKey(DependencyObject element, int value)
        {
            element.SetValue(RefreshKeyProperty, value);
        }

        public static int GetRefreshKey(DependencyObject element)
        {
            return (int)element.GetValue(RefreshKeyProperty);
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

            Apply(
                block,
                GetText(block),
                GetMentionedJids(block) as IEnumerable<string>,
                GetMentionLookup(block) as IReadOnlyDictionary<string, string>);
        }

        /// <summary>
        /// Resolves @digits mentions to display names for plain TextBlock previews
        /// (chat list strip). Does not build rich runs — layout stays reliable in list rows.
        /// </summary>
        public static string FormatMentionsPlain(
            string text,
            IEnumerable<string> mentionedJids = null,
            IReadOnlyDictionary<string, string> mentionLookup = null)
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

            IReadOnlyDictionary<string, string> lookup = BindLookup(mentionLookup, mentionedJids, whatsApp);
            if ((lookup == null || lookup.Count == 0) && whatsApp == null)
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

                string token = m.Groups[1].Value;
                string digits = MentionLookupBuilder.ExtractUserDigits(token);
                string displayName = ResolveMentionDisplayName(digits, lookup, whatsApp);
                if (MentionLookupBuilder.IsUsableName(displayName, digits))
                {
                    sb.Append('@');
                    sb.Append(MentionLookupBuilder.CleanLabel(displayName));
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

        public static void Apply(
            RichTextBlock block,
            string text,
            IEnumerable<string> mentionedJids = null,
            IReadOnlyDictionary<string, string> mentionLookup = null)
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

            IReadOnlyDictionary<string, string> lookup = BindLookup(mentionLookup, mentionedJids, whatsApp);
            var segments = MessageLinkParser.Parse(text);
            for (int i = 0; i < segments.Count; i++)
            {
                MessageLinkParser.Segment segment = segments[i];
                if (segment.IsLink)
                {
                    Uri uri;
                    if (!Uri.TryCreate(segment.NavigateUrl, UriKind.Absolute, out uri))
                    {
                        AppendPlainWithMentions(paragraph, segment.Text, lookup, whatsApp, mentionBrush);
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
                    AppendPlainWithMentions(paragraph, segment.Text, lookup, whatsApp, mentionBrush);
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

        private static IReadOnlyDictionary<string, string> BindLookup(
            IReadOnlyDictionary<string, string> rosterLookup,
            IEnumerable<string> mentionedJids,
            IWhatsAppService whatsApp)
        {
            IReadOnlyDictionary<string, string> roster = rosterLookup ?? MentionLookupBuilder.Empty;
            if (mentionedJids == null)
            {
                return roster;
            }

            bool any = false;
            foreach (string unused in mentionedJids)
            {
                any = true;
                break;
            }

            if (!any)
            {
                return roster;
            }

            Func<string, string> canonical = null;
            if (whatsApp != null)
            {
                canonical = jid =>
                {
                    try
                    {
                        return whatsApp.GetCanonicalJid(jid);
                    }
                    catch
                    {
                        return null;
                    }
                };
            }

            return MentionLookupBuilder.OverlayMentionedJids(roster, mentionedJids, canonical);
        }

        private static string ResolveMentionDisplayName(
            string digits,
            IReadOnlyDictionary<string, string> mentionLookup,
            IWhatsAppService whatsApp)
        {
            string fromLookup = MentionLookupBuilder.FindName(mentionLookup, digits);
            if (MentionLookupBuilder.IsUsableName(fromLookup, digits))
            {
                return MentionLookupBuilder.CleanLabel(fromLookup);
            }

            if (whatsApp == null || string.IsNullOrEmpty(digits))
            {
                return null;
            }

            string fromPn = SafeResolveName(whatsApp, digits + "@s.whatsapp.net");
            if (MentionLookupBuilder.IsUsableName(fromPn, digits))
            {
                return MentionLookupBuilder.CleanLabel(fromPn);
            }

            string fromLid = SafeResolveName(whatsApp, digits + "@lid");
            if (MentionLookupBuilder.IsUsableName(fromLid, digits))
            {
                return MentionLookupBuilder.CleanLabel(fromLid);
            }

            return null;
        }

        private static void AppendPlainWithMentions(
            Paragraph paragraph,
            string text,
            IReadOnlyDictionary<string, string> mentionLookup,
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

                string token = m.Groups[1].Value;
                string digits = MentionLookupBuilder.ExtractUserDigits(token);
                string displayName = ResolveMentionDisplayName(digits, mentionLookup, whatsApp);
                if (MentionLookupBuilder.IsUsableName(displayName, digits))
                {
                    paragraph.Inlines.Add(new Run
                    {
                        Text = "@" + MentionLookupBuilder.CleanLabel(displayName),
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
