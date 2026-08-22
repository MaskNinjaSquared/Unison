using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Uwp.UI.Helpers;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Imaging;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Chat-list subtitle: outgoing ticks + optional group author + media chip + preview text.
    /// Bind <see cref="Preview"/> (chat list) or the individual Kind/Text/Author props (quotes).
    /// </summary>
    public sealed partial class ChatListPreviewStrip : UserControl
    {
        private const double MinTrailingWidth = 28;
        private long _foregroundToken = -1;
        private ChatListPreview _previewHooked;

        public static readonly DependencyProperty PreviewProperty =
            DependencyProperty.Register(
                nameof(Preview),
                typeof(ChatListPreview),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(null, OnPreviewChanged));

        public static readonly DependencyProperty KindProperty =
            DependencyProperty.Register(
                nameof(Kind),
                typeof(ChatPreviewKind),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(ChatPreviewKind.Text, OnLayoutInputsChanged));

        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(string.Empty, OnPreviewTextInputsChanged));

        public static readonly DependencyProperty AuthorProperty =
            DependencyProperty.Register(
                nameof(Author),
                typeof(string),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(string.Empty, OnLayoutInputsChanged));

        public static readonly DependencyProperty MentionedJidsProperty =
            DependencyProperty.Register(
                nameof(MentionedJids),
                typeof(object),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(null, OnPreviewTextInputsChanged));

        public static readonly DependencyProperty MentionLookupProperty =
            DependencyProperty.Register(
                nameof(MentionLookup),
                typeof(object),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(null, OnPreviewTextInputsChanged));

        public ChatListPreviewStrip()
        {
            InitializeComponent();
            _foregroundToken = RegisterPropertyChangedCallback(ForegroundProperty, OnForegroundPropertyChanged);
            Loaded += (_, __) =>
            {
                ApplyPreviewModel();
                RefreshPreviewText();
                SyncKindHostForeground();
                Relayout();
            };
            Unloaded += (_, __) =>
            {
                HookPreview(null);
                if (_foregroundToken >= 0)
                {
                    UnregisterPropertyChangedCallback(ForegroundProperty, _foregroundToken);
                    _foregroundToken = -1;
                }
            };
        }

        public ChatListPreview Preview
        {
            get { return (ChatListPreview)GetValue(PreviewProperty); }
            set { SetValue(PreviewProperty, value); }
        }

        public ChatPreviewKind Kind
        {
            get { return (ChatPreviewKind)GetValue(KindProperty); }
            set { SetValue(KindProperty, value); }
        }

        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        public string Author
        {
            get { return (string)GetValue(AuthorProperty); }
            set { SetValue(AuthorProperty, value); }
        }

        public object MentionedJids
        {
            get { return GetValue(MentionedJidsProperty); }
            set { SetValue(MentionedJidsProperty, value); }
        }

        public object MentionLookup
        {
            get { return GetValue(MentionLookupProperty); }
            set { SetValue(MentionLookupProperty, value); }
        }

        private static void OnPreviewChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as ChatListPreviewStrip;
            if (strip == null)
            {
                return;
            }

            strip.HookPreview(e.NewValue as ChatListPreview);
            strip.ApplyPreviewModel();
            strip.RefreshPreviewText();
            strip.Relayout();
        }

        private static void OnPreviewTextInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as ChatListPreviewStrip;
            if (strip == null)
            {
                return;
            }

            strip.RefreshPreviewText();
            strip.Relayout();
        }

        private static void OnLayoutInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as ChatListPreviewStrip;
            if (strip == null)
            {
                return;
            }

            if (strip.KindHost != null)
            {
                strip.KindHost.LayoutUpdated -= strip.KindHost_LayoutUpdated;
                strip.KindHost.LayoutUpdated += strip.KindHost_LayoutUpdated;
            }

            strip.Relayout();
        }

        private void HookPreview(ChatListPreview preview)
        {
            if (_previewHooked != null)
            {
                _previewHooked.PropertyChanged -= Preview_PropertyChanged;
            }

            _previewHooked = preview;
            if (_previewHooked != null)
            {
                _previewHooked.PropertyChanged += Preview_PropertyChanged;
            }
        }

        private void Preview_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            ApplyPreviewModel();
            RefreshPreviewText();
            Relayout();
        }

        private void ApplyPreviewModel()
        {
            ChatListPreview preview = Preview;
            if (preview != null)
            {
                Kind = preview.Kind;
                Text = preview.Text;
                Author = preview.Author;
                MentionedJids = preview.MentionedJids;
            }

            ApplyStatusChrome(preview);
        }

        private void ApplyStatusChrome(ChatListPreview preview)
        {
            if (StatusHost == null || StatusImage == null || FailedMark == null)
            {
                return;
            }

            bool showTick = preview != null && preview.ShowStatusCheckmark;
            bool showFail = preview != null && preview.ShowSendFailed;
            StatusHost.Visibility = (showTick || showFail) ? Visibility.Visible : Visibility.Collapsed;
            FailedMark.Visibility = showFail ? Visibility.Visible : Visibility.Collapsed;
            StatusImage.Visibility = showTick ? Visibility.Visible : Visibility.Collapsed;
            if (showTick && !string.IsNullOrEmpty(preview.StatusCheckmarkUri))
            {
                StatusImage.Source = new BitmapImage(new System.Uri(preview.StatusCheckmarkUri));
            }
            else
            {
                StatusImage.Source = null;
            }
        }

        private void KindHost_LayoutUpdated(object sender, object e)
        {
            if (KindHost != null)
            {
                KindHost.LayoutUpdated -= KindHost_LayoutUpdated;
            }

            SyncKindHostForeground();
        }

        private static void OnForegroundPropertyChanged(DependencyObject sender, DependencyProperty dp)
        {
            (sender as ChatListPreviewStrip)?.SyncKindHostForeground();
        }

        private void RootLayout_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            Relayout();
        }

        private void RefreshPreviewText()
        {
            if (PreviewTextBlock == null)
            {
                return;
            }

            PreviewTextBlock.Text = CommentRichService.FormatMentionsPlain(
                Text,
                MentionedJids as IEnumerable<string>,
                MentionLookup as IReadOnlyDictionary<string, string>);
        }

        private void SyncKindHostForeground()
        {
            if (KindHost == null)
            {
                return;
            }

            Brush brush = Foreground;
            KindHost.Foreground = brush;
            if (Kind == ChatPreviewKind.Voice || Kind == ChatPreviewKind.Text || brush == null)
            {
                return;
            }

            KindHost.UpdateLayout();
            ApplyForegroundToKindChip(KindHost, brush);
        }

        private static void ApplyForegroundToKindChip(DependencyObject root, Brush brush)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                var icon = child as FontIcon;
                if (icon != null)
                {
                    icon.Foreground = brush;
                }

                var text = child as TextBlock;
                if (text != null)
                {
                    text.Foreground = brush;
                }

                ApplyForegroundToKindChip(child, brush);
            }
        }

        private void Relayout()
        {
            if (RootLayout == null || AuthorBlock == null || KindHost == null || PreviewTextBlock == null)
            {
                return;
            }

            double available = RootLayout.ActualWidth;
            if (available <= 0 || double.IsNaN(available))
            {
                return;
            }

            AuthorBlock.ClearValue(FrameworkElement.MaxWidthProperty);
            KindHost.ClearValue(FrameworkElement.MaxWidthProperty);
            PreviewTextBlock.ClearValue(FrameworkElement.MaxWidthProperty);

            var infinite = new Size(double.PositiveInfinity, double.PositiveInfinity);
            double statusWidth = 0;
            if (StatusHost != null && StatusHost.Visibility == Visibility.Visible)
            {
                StatusHost.Measure(infinite);
                statusWidth = StatusHost.DesiredSize.Width;
            }

            AuthorBlock.Measure(infinite);
            KindHost.Measure(infinite);
            PreviewTextBlock.Measure(infinite);

            bool hasAuthor = AuthorBlock.Visibility == Visibility.Visible &&
                             !string.IsNullOrEmpty(AuthorBlock.Text);
            bool hasChip = Kind != ChatPreviewKind.Text;
            bool hasText = !string.IsNullOrEmpty(PreviewTextBlock.Text);

            double authorDesired = hasAuthor ? AuthorBlock.DesiredSize.Width : 0;
            double chipDesired = hasChip ? KindHost.DesiredSize.Width : 0;
            double leftover = System.Math.Max(0, available - statusWidth);

            double reserveForTrailing = 0;
            if (hasChip || hasText)
            {
                reserveForTrailing = MinTrailingWidth;
            }

            double authorWidth = authorDesired;
            if (hasAuthor)
            {
                double authorMax = System.Math.Max(0, leftover - reserveForTrailing);
                authorWidth = System.Math.Min(authorDesired, authorMax);
                AuthorBlock.MaxWidth = authorWidth;
            }

            double remaining = System.Math.Max(0, leftover - authorWidth);
            if (hasChip && hasText)
            {
                double chipWidth = System.Math.Min(chipDesired, remaining);
                if (remaining > MinTrailingWidth && chipWidth > remaining - 12)
                {
                    chipWidth = System.Math.Max(MinTrailingWidth, remaining - 12);
                }

                KindHost.MaxWidth = chipWidth;
                PreviewTextBlock.MaxWidth = System.Math.Max(0, remaining - chipWidth);
            }
            else if (hasChip)
            {
                KindHost.MaxWidth = remaining;
            }
            else if (hasText)
            {
                PreviewTextBlock.MaxWidth = remaining;
            }
        }
    }
}
