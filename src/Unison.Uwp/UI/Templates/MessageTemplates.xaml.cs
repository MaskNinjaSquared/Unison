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

        /// <summary>
        /// Keep download/play media tiles square: stretch up to 250×250, shrink on narrow phones.
        /// </summary>
        private const double MediaPlaceholderMaxSide = 250;

        private void MediaPlaceholder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            var grid = sender as FrameworkElement;
            if (grid == null)
            {
                return;
            }

            double width = e.NewSize.Width;
            if (width <= 0 || double.IsNaN(width))
            {
                return;
            }

            double side = width > MediaPlaceholderMaxSide ? MediaPlaceholderMaxSide : width;

            if (System.Math.Abs(grid.Height - side) > 0.5)
            {
                grid.Height = side;
            }
        }

        private void DocumentReady_RightTapped(object sender, RightTappedRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnDocumentReadyContextRequested(sender, e);
        }

        private void DocumentReady_Holding(object sender, HoldingRoutedEventArgs e)
        {
            FindChatDetail(sender)?.OnDocumentReadyHolding(sender, e);
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
