using System;
using Unison.Uwp.UI.Controls;
using Unison.Uwp.UI.Views;
using Unison.Core.Helpers;
using Unison.Core.ViewModels;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Templates
{
    public sealed partial class MessageTemplates : ResourceDictionary
    {
        private const string ReplySwipeHandleTag = "ReplySwipeHandle";
        private const double BubbleMinMaxWidth = 120;

        public MessageTemplates()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Stretch slot width comes from the ListViewItem; push that onto the Auto-sized
        /// bubble content so WrapWholeWords can break. Never bind MaxWidth to the content's
        /// own ActualWidth (circular → 0 → invisible bubbles).
        /// </summary>
        private void MessageBubbleSlot_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyBubbleContentMaxWidth(sender as FrameworkElement);
        }

        private void MessageBubbleSlot_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.PreviousSize.Width == e.NewSize.Width)
            {
                return;
            }

            ApplyBubbleContentMaxWidth(sender as FrameworkElement);
        }

        private static void ApplyBubbleContentMaxWidth(FrameworkElement slot)
        {
            if (slot == null)
            {
                return;
            }

            double width = slot.ActualWidth;
            if (width <= 0)
            {
                width = ResolveAncestorContentWidth(slot);
            }

            if (width < BubbleMinMaxWidth)
            {
                return;
            }

            FrameworkElement content = null;
            if (VisualTreeHelper.GetChildrenCount(slot) > 0)
            {
                content = VisualTreeHelper.GetChild(slot, 0) as FrameworkElement;
            }

            if (content == null)
            {
                return;
            }

            if (Math.Abs(content.MaxWidth - width) < 0.5)
            {
                return;
            }

            content.MaxWidth = width;
        }

        private static double ResolveAncestorContentWidth(DependencyObject start)
        {
            DependencyObject current = start;
            while (current != null)
            {
                var item = current as ListViewItem;
                if (item != null && item.ActualWidth > 0)
                {
                    return item.ActualWidth;
                }

                var list = current as ListView;
                if (list != null && list.ActualWidth > 0)
                {
                    return list.ActualWidth;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            try
            {
                double screen = Window.Current?.Bounds.Width ?? 0;
                if (screen > BubbleMinMaxWidth)
                {
                    return Math.Max(BubbleMinMaxWidth, screen - 60);
                }
            }
            catch
            {
            }

            return 0;
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

        private void MessageBubble_ManipulationDelta(object sender, ManipulationDeltaRoutedEventArgs e)
        {
            var root = sender as UIElement;
            if (root == null)
            {
                return;
            }

            double dx = e.Cumulative.Translation.X;
            // Bubble sling: follow the finger horizontally (leftward reply gesture).
            EnsureTranslate(root).X = dx < 0 ? dx : 0;
            UIElement handle = FindTaggedChild(root, ReplySwipeHandleTag);
            if (handle != null)
            {
                handle.Opacity = MessageBubbleReplySwipe.Progress(dx);
            }
        }

        private void MessageBubble_ManipulationCompleted(object sender, ManipulationCompletedRoutedEventArgs e)
        {
            ResetReplySwipeVisuals(sender as UIElement);

            if (!MessageBubbleReplySwipe.ShouldReply(
                    e.Cumulative.Translation.X,
                    e.Cumulative.Translation.Y))
            {
                return;
            }

            var bubble = sender as FrameworkElement;
            var message = bubble?.DataContext as ChatMessageViewModel;
            if (message == null)
            {
                return;
            }

            FindChatDetail(sender)?.ViewModel?.BeginReply(message);
            e.Handled = true;
        }

        private static void ResetReplySwipeVisuals(UIElement root)
        {
            if (root == null)
            {
                return;
            }

            EnsureTranslate(root).X = 0;
            UIElement handle = FindTaggedChild(root, ReplySwipeHandleTag);
            if (handle != null)
            {
                handle.Opacity = 0;
            }
        }

        private static TranslateTransform EnsureTranslate(UIElement element)
        {
            var translate = element.RenderTransform as TranslateTransform;
            if (translate == null)
            {
                translate = new TranslateTransform();
                element.RenderTransform = translate;
            }

            return translate;
        }

        private static UIElement FindTaggedChild(DependencyObject root, string tag)
        {
            if (root == null || string.IsNullOrEmpty(tag))
            {
                return null;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var element = child as FrameworkElement;
                if (element != null &&
                    string.Equals(element.Tag as string, tag, System.StringComparison.Ordinal))
                {
                    return element;
                }

                UIElement nested = FindTaggedChild(child, tag);
                if (nested != null)
                {
                    return nested;
                }
            }

            return null;
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
