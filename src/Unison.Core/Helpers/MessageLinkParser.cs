using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Plain-text URL segmentation for rich bubbles / captions (Imgur-style comment rich).
    /// Platform-free — UWP turns <see cref="Link"/> segments into underlined Hyperlinks.
    /// </summary>
    public static class MessageLinkParser
    {
        // http(s) or www. — stop at whitespace / common wrappers.
        private static readonly Regex UrlRegex = new Regex(
            @"(?:https?://|www\.)[^\s<>""'\]]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public sealed class Segment
        {
            public Segment(string text, bool isLink, string navigateUrl)
            {
                Text = text ?? string.Empty;
                IsLink = isLink;
                NavigateUrl = navigateUrl;
            }

            public string Text { get; }
            public bool IsLink { get; }
            public string NavigateUrl { get; }
        }

        public static IReadOnlyList<Segment> Parse(string text)
        {
            var list = new List<Segment>();
            if (string.IsNullOrEmpty(text))
            {
                return list;
            }

            MatchCollection matches = UrlRegex.Matches(text);
            int index = 0;
            for (int i = 0; i < matches.Count; i++)
            {
                Match m = matches[i];
                if (m.Index > index)
                {
                    list.Add(new Segment(text.Substring(index, m.Index - index), false, null));
                }

                string raw = TrimTrailingPunctuation(m.Value);
                if (raw.Length == 0)
                {
                    index = m.Index + m.Length;
                    continue;
                }

                // If we trimmed punctuation, keep the leftover as plain text after the link.
                int consumed = raw.Length;
                string navigate = NormalizeNavigateUrl(raw);
                if (navigate != null)
                {
                    list.Add(new Segment(raw, true, navigate));
                }
                else
                {
                    list.Add(new Segment(raw, false, null));
                }

                int end = m.Index + consumed;
                if (end < m.Index + m.Length)
                {
                    list.Add(new Segment(
                        text.Substring(end, (m.Index + m.Length) - end),
                        false,
                        null));
                }

                index = m.Index + m.Length;
            }

            if (index < text.Length)
            {
                list.Add(new Segment(text.Substring(index), false, null));
            }

            return list;
        }

        private static string TrimTrailingPunctuation(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return value;
            }

            int end = value.Length;
            while (end > 0)
            {
                char c = value[end - 1];
                if (c == '.' || c == ',' || c == ';' || c == ':' || c == '!' ||
                    c == '?' || c == ')' || c == ']' || c == '}' || c == '"' || c == '\'')
                {
                    end--;
                    continue;
                }

                break;
            }

            return end == value.Length ? value : value.Substring(0, end);
        }

        private static string NormalizeNavigateUrl(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            string url = raw.Trim();
            if (url.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                return null;
            }

            if (!string.Equals(uri.Scheme, "http", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return uri.AbsoluteUri;
        }
    }
}
