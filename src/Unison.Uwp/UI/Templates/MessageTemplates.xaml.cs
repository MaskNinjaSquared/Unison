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
        /// Media host Grid (not StackPanel): set Width+Height up to 250 from the
        /// messages ListView so the tile fills available width on small phones too.
        /// Children stretch inside the host.
        /// </summary>
        private const double MediaPlaceholderMaxSide = 250;

        /// <summary>Outer bubble margins (48+12) plus content padding (12+16).</summary>
        private const double MediaPlaceholderChromeInset = 88;

        private void MediaPlaceholder_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyMediaPlaceholderSize(sender as FrameworkElement);
        }

        private void MediaPlaceholder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // Ignore self-inflicted height/width updates once we already match the target.
            var grid = sender as FrameworkElement;
            if (grid == null)
            {
                return;
            }

            double paneWidth = ResolveMessagesPaneWidth(grid);
            if (paneWidth <= 0)
            {
                return;
            }

            double available = paneWidth > MediaPlaceholderChromeInset
                ? paneWidth - MediaPlaceholderChromeInset
                : paneWidth;
            double side = available > MediaPlaceholderMaxSide
                ? MediaPlaceholderMaxSide
                : available;

            if (System.Math.Abs(e.NewSize.Width - side) <= 0.5 &&
                System.Math.Abs(e.NewSize.Height - side) <= 0.5)
            {
                return;
            }

            ApplyMediaPlaceholderSize(grid);
        }

        private static void ApplyMediaPlaceholderSize(FrameworkElement grid)
        {
            if (grid == null)
            {
                return;
            }

            double paneWidth = ResolveMessagesPaneWidth(grid);
            double available = paneWidth > MediaPlaceholderChromeInset
                ? paneWidth - MediaPlaceholderChromeInset
                : paneWidth;

            if (available <= 0 || double.IsNaN(available))
            {
                return;
            }

            double side = available > MediaPlaceholderMaxSide
                ? MediaPlaceholderMaxSide
                : available;

            if (System.Math.Abs(grid.Width - side) > 0.5)
            {
                grid.Width = side;
            }

            if (System.Math.Abs(grid.Height - side) > 0.5)
            {
                grid.Height = side;
            }
        }

        private static double ResolveMessagesPaneWidth(FrameworkElement from)
        {
            DependencyObject current = from;
            while (current != null)
            {
                var list = current as Windows.UI.Xaml.Controls.ListView;
                if (list != null && list.ActualWidth > 0)
                {
                    return list.ActualWidth;
                }

                var items = current as Windows.UI.Xaml.Controls.ItemsControl;
                if (items != null && items.ActualWidth > 120)
                {
                    return items.ActualWidth;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return 0;
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
