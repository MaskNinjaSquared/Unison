using System;
using System.Globalization;
using System.Text;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Messages;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Controls;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Documents;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Templates
{
    public sealed partial class MessageTemplates : ResourceDictionary
    {
        private const string ReplySwipeHandleTag = "ReplySwipeHandle";
        private const double BubbleMinMaxWidth = 120;

        /// <summary>Unigram <c>MessageMaxWidth</c> — ceiling so HA Left/Right measure still wraps.</summary>
        private const double MessageMaxWidth = 432;

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

            // Unigram: bubble MaxWidth = min(available, MessageMaxWidth). Without the slot
            // cap, HA Left/Right + MaxWidth 432 alone still overflows narrow phone viewports.
            double capped = Math.Min(width, MessageMaxWidth);

            FrameworkElement content = null;
            if (VisualTreeHelper.GetChildrenCount(slot) > 0)
            {
                content = VisualTreeHelper.GetChild(slot, 0) as FrameworkElement;
            }

            if (content == null)
            {
                return;
            }

            SetMaxWidthIfChanged(content, capped);

            // Auto-column hosts still measure children with infinity — same ceiling as Unigram's
            // MessageBubble.MaxWidth must live on the panel that wraps the RichTextBlock.
            FrameworkElement panel = FindNamedDescendant(content, "SentBubblePanel")
                ?? FindNamedDescendant(content, "ReceivedBubblePanel");
            if (panel != null)
            {
                SetMaxWidthIfChanged(panel, capped);
            }
        }

        private static void SetMaxWidthIfChanged(FrameworkElement element, double width)
        {
            if (element == null)
            {
                return;
            }

            if (Math.Abs(element.MaxWidth - width) < 0.5)
            {
                return;
            }

            element.MaxWidth = width;
        }

        private static FrameworkElement FindNamedDescendant(DependencyObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var fe = root as FrameworkElement;
            if (fe != null && string.Equals(fe.Name, name, StringComparison.Ordinal))
            {
                return fe;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                FrameworkElement hit = FindNamedDescendant(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
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

        /// <summary>
        /// DEBUG: tap a bubble → dump string + layout metrics (single-line overflow).
        /// Does not mark Handled so selection / other gestures still work.
        /// </summary>
        private void MessageBubble_Tapped(object sender, TappedRoutedEventArgs e)
        {
            var slot = sender as FrameworkElement;
            var vm = slot?.DataContext as ChatMessageViewModel;
            if (vm?.Model == null)
            {
                return;
            }

            MessageBubbleContentProbe.DumpTap(vm.Model, DescribeBubbleLayout(slot, vm));
        }

        private static string DescribeBubbleLayout(FrameworkElement slot, ChatMessageViewModel vm)
        {
            if (slot == null)
            {
                return "slot=null";
            }

            var sb = new StringBuilder(256);
            sb.Append("slot=").Append(slot.Name ?? "(anon)");
            sb.Append(" slotW=").Append(F(slot.ActualWidth));
            sb.Append(" slotH=").Append(F(slot.ActualHeight));
            sb.Append(" contentMaxLines=").Append(vm != null ? vm.ContentMaxLines : -1);
            sb.Append(" canExpand=").Append(vm != null && vm.CanExpand);
            sb.Append(" isExpanded=").Append(vm != null && vm.IsExpanded);

            FrameworkElement content = null;
            if (VisualTreeHelper.GetChildrenCount(slot) > 0)
            {
                content = VisualTreeHelper.GetChild(slot, 0) as FrameworkElement;
            }

            if (content != null)
            {
                sb.Append(" contentName=").Append(content.Name ?? "(anon)");
                sb.Append(" contentMaxW=").Append(F(content.MaxWidth));
                sb.Append(" contentW=").Append(F(content.ActualWidth));
                sb.Append(" contentH=").Append(F(content.ActualHeight));
                sb.Append(" contentDesiredW=").Append(F(content.DesiredSize.Width));
            }
            else
            {
                sb.Append(" content=(missing)");
            }

            RichTextBlock body = FindNamedRichText(slot, "SentBodyText")
                ?? FindNamedRichText(slot, "ReceivedBodyText")
                ?? FindFirstRichText(slot);
            if (body != null)
            {
                sb.Append(" bodyName=").Append(body.Name ?? "(anon)");
                sb.Append(" bodyWrap=").Append(body.TextWrapping);
                sb.Append(" bodyMaxLines=").Append(body.MaxLines);
                sb.Append(" bodyW=").Append(F(body.ActualWidth));
                sb.Append(" bodyH=").Append(F(body.ActualHeight));
                sb.Append(" bodyDesiredW=").Append(F(body.DesiredSize.Width));
                sb.Append(" bodyMaxW=").Append(F(body.MaxWidth));
                sb.Append(" bodyAvailW=");
                try
                {
                    sb.Append(F(body.ActualWidth > 0 ? body.ActualWidth : double.NaN));
                }
                catch
                {
                    sb.Append("?");
                }

                int runChars = 0;
                int paraCount = 0;
                try
                {
                    foreach (Block block in body.Blocks)
                    {
                        var para = block as Paragraph;
                        if (para == null)
                        {
                            continue;
                        }

                        paraCount++;
                        foreach (Inline inline in para.Inlines)
                        {
                            var run = inline as Run;
                            if (run?.Text != null)
                            {
                                runChars += run.Text.Length;
                            }
                        }
                    }
                }
                catch
                {
                }

                sb.Append(" paras=").Append(paraCount);
                sb.Append(" runChars=").Append(runChars);
            }
            else
            {
                sb.Append(" body=(none)");
            }

            double ancestor = ResolveAncestorContentWidth(slot);
            sb.Append(" ancestorW=").Append(F(ancestor));
            return sb.ToString();
        }

        private static string F(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return value.ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        private static RichTextBlock FindNamedRichText(DependencyObject root, string name)
        {
            if (root == null || string.IsNullOrEmpty(name))
            {
                return null;
            }

            var fe = root as FrameworkElement;
            if (fe != null && string.Equals(fe.Name, name, StringComparison.Ordinal))
            {
                return root as RichTextBlock;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                RichTextBlock hit = FindNamedRichText(VisualTreeHelper.GetChild(root, i), name);
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
        }

        private static RichTextBlock FindFirstRichText(DependencyObject root)
        {
            if (root == null)
            {
                return null;
            }

            var direct = root as RichTextBlock;
            if (direct != null &&
                (string.Equals(direct.Name, "SentBodyText", StringComparison.Ordinal) ||
                 string.Equals(direct.Name, "ReceivedBodyText", StringComparison.Ordinal) ||
                 string.Equals(direct.Name, "SentCaption", StringComparison.Ordinal) ||
                 string.Equals(direct.Name, "ReceivedCaption", StringComparison.Ordinal)))
            {
                return direct;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                RichTextBlock hit = FindFirstRichText(VisualTreeHelper.GetChild(root, i));
                if (hit != null)
                {
                    return hit;
                }
            }

            // Prefer body-named; fall back to any RichTextBlock last.
            return FindAnyRichText(root);
        }

        private static RichTextBlock FindAnyRichText(DependencyObject root)
        {
            var direct = root as RichTextBlock;
            if (direct != null)
            {
                return direct;
            }

            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                RichTextBlock hit = FindAnyRichText(VisualTreeHelper.GetChild(root, i));
                if (hit != null)
                {
                    return hit;
                }
            }

            return null;
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
