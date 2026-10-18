using Unison.Uwp.UI.Controls;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Templates
{
    public sealed partial class MessageTemplates : ResourceDictionary
    {
        public MessageTemplates()
        {
            InitializeComponent();
        }

        /// <summary>Legacy shell: swap Segoe play for the Legacy icon font on video overlays.</summary>
        private void VideoPlayGlyph_Loaded(object sender, RoutedEventArgs e)
        {
            var label = sender as TextBlock;
            if (label == null || !ShellUi.IsWhatsAppLegacy)
            {
                return;
            }

            FontFamily family = LegacyMediaGlyphs.IconFont;
            if (family == null)
            {
                return;
            }

            label.FontFamily = family;
            label.Text = LegacyMediaGlyphs.Play;
            label.TextLineBounds = TextLineBounds.Tight;
            label.Margin = new Thickness(0);
        }

        private void MessageBubble_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnMessageBubbleRightTapped(sender, e);
        }

        private void MessageBubble_Holding(object sender, HoldingRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnMessageBubbleHolding(sender, e);
        }

        private void QuoteBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnQuotedMessageTapped(sender, e);
        }

        private void QuotedAuthor_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnQuotedAuthorTapped(sender, e);
        }

        private void GroupParticipant_Tapped(object sender, TappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnGroupParticipantTapped(sender, e);
        }

        private void AudioPlayButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnAudioPlayButtonClick(sender, e);
        }

        private void ImageOpenButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnImageOpenButtonClick(sender, e);
        }

        private void VideoOpenButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnVideoOpenButtonClick(sender, e);
        }

        private void DocumentReady_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnDocumentReadyContextRequested(sender, e);
        }

        private void DocumentReady_Holding(object sender, HoldingRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnDocumentReadyHolding(sender, e);
        }

        private void DocumentOverflow_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnDocumentOverflowClick(sender, e);
        }

        private static IChatDetailSurface FindChatDetail(object sender)
        {
            var current = sender as DependencyObject;
            while (current != null)
            {
                var view = current as IChatDetailSurface;
                if (view != null)
                {
                    return view;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }
    }
}
