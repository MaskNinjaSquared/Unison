using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.UI.Helpers
{
    /// <summary>
    /// <c>helpers:EmojiTextBlock.Text</c>: plain text on a <see cref="TextBlock"/> with emoji
    /// graphemes on the WhatsApp face (<see cref="EmojiRichRuns"/>).
    /// </summary>
    public static class EmojiTextBlock
    {
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.RegisterAttached(
                "Text",
                typeof(string),
                typeof(EmojiTextBlock),
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
            EmojiRichRuns.SetTextBlock(d as TextBlock, e.NewValue as string);
        }
    }
}
