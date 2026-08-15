using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
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

        private static ChatDetailView FindChatDetail(object sender)
        {
            var current = sender as DependencyObject;
            while (current != null)
            {
                var view = current as ChatDetailView;
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
