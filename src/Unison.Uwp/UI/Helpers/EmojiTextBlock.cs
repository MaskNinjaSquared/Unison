using System.Collections.Generic;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// <c>helpers:EmojiTextBlock.Text</c>: plain text on a <see cref="TextBlock"/> with emoji
    /// graphemes on the WhatsApp face (<see cref="EmojiRichRuns"/>).
    /// <para>
    /// Optionally set <c>MentionedJids</c>/<c>MentionLookup</c> to resolve <c>@digits</c> to
    /// display names first, the same way <c>ChatListPreviewStrip</c> paints one-line previews.
    /// Mention resolution is opt-in so list surfaces that only need emoji stay cheap, and URLs
    /// are never linkified here — use <see cref="CommentRichService"/> when live links are wanted.
    /// </para>
    /// </summary>
    public static class EmojiTextBlock
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(EmojiTextBlock),
                new PropertyMetadata(null, OnRenderInputChanged));

        public static readonly DependencyProperty MentionedJidsProperty =
            DependencyProperty.RegisterAttached(
                "MentionedJids",
                typeof(object),
                typeof(EmojiTextBlock),
                new PropertyMetadata(null, OnRenderInputChanged));

        public static readonly DependencyProperty MentionLookupProperty =
            DependencyProperty.RegisterAttached(
                "MentionLookup",
                typeof(object),
                typeof(EmojiTextBlock),
                new PropertyMetadata(null, OnRenderInputChanged));

        /// <summary>Bump to repaint when the roster lookup is replaced in place.</summary>
        public static readonly DependencyProperty RefreshKeyProperty =
            DependencyProperty.RegisterAttached(
                "RefreshKey",
                typeof(int),
                typeof(EmojiTextBlock),
                new PropertyMetadata(0, OnRenderInputChanged));

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

        private static void OnRenderInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            Render(d as TextBlock);
        }

        private static void Render(TextBlock block)
        {
            if (block == null)
            {
                return;
            }

            string text = GetText(block);
            object jids = block.GetValue(MentionedJidsProperty);
            object lookup = block.GetValue(MentionLookupProperty);
            if (jids != null || lookup != null)
            {
                text = CommentRichService.FormatMentionsPlain(
                    text,
                    jids as IEnumerable<string>,
                    lookup as IReadOnlyDictionary<string, string>);
            }

            EmojiRichRuns.SetTextBlock(block, text);
        }
    }
}
