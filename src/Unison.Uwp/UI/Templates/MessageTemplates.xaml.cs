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

        private void AudioButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnAudioButtonClick(sender, e);
        }

        private void ImageDownloadButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnImageDownloadButtonClick(sender, e);
        }

        private void ImageOpenButton_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnImageOpenButtonClick(sender, e);
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
