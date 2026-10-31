using System.Collections.Generic;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Uwp.UI.Helpers;
using Windows.Foundation;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

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

        public static readonly DependencyProperty AuthorDetailProperty =
            DependencyProperty.Register(
                nameof(AuthorDetail),
                typeof(string),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(string.Empty, OnLayoutInputsChanged));

        public static readonly DependencyProperty ShowAuthorChevronProperty =
            DependencyProperty.Register(
                nameof(ShowAuthorChevron),
                typeof(bool),
                typeof(ChatListPreviewStrip),
                new PropertyMetadata(false, OnLayoutInputsChanged));

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
                RefreshStripText();
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

        public string AuthorDetail
        {
            get { return (string)GetValue(AuthorDetailProperty); }
            set { SetValue(AuthorDetailProperty, value); }
        }

        public bool ShowAuthorChevron
        {
            get { return (bool)GetValue(ShowAuthorChevronProperty); }
            set { SetValue(ShowAuthorChevronProperty, value); }
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
            strip.RefreshStripText();
            strip.Relayout();
        }

        private static void OnPreviewTextInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as ChatListPreviewStrip;
            if (strip == null)
            {
                return;
            }

            strip.RefreshStripText();
            strip.Relayout();
        }

        private static void OnLayoutInputsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var strip = d as ChatListPreviewStrip;
            if (strip == null)
            {
                return;
            }

            if (e.Property == AuthorProperty || e.Property == AuthorDetailProperty)
            {
                strip.RefreshAuthorRuns();
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
            RefreshStripText();
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
                AuthorDetail = preview.AuthorDetail;
                ShowAuthorChevron = preview.ShowAuthorChevron;
                MentionedJids = preview.MentionedJids;
            }
            else
            {
                AuthorDetail = string.Empty;
                ShowAuthorChevron = false;
            }

            ApplyStatusChrome(preview);
        }

        private void ApplyStatusChrome(ChatListPreview preview)
        {
            if (StatusHost == null || StatusIconSent == null || StatusIconRead == null || FailedMark == null)
            {
                return;
            }

            bool showTick = preview != null && preview.ShowStatusCheckmark;
            bool showFail = preview != null && preview.ShowSendFailed;
            bool showRead = showTick && preview.IsReadStatus;
            bool showSent = showTick && !preview.IsReadStatus;
            StatusHost.Visibility = (showTick || showFail) ? Visibility.Visible : Visibility.Collapsed;
            FailedMark.Visibility = showFail ? Visibility.Visible : Visibility.Collapsed;
            StatusIconSent.Visibility = showSent ? Visibility.Visible : Visibility.Collapsed;
            StatusIconRead.Visibility = showRead ? Visibility.Visible : Visibility.Collapsed;

            var glyph = showTick ? (preview.StatusGlyph ?? string.Empty) : string.Empty;
            StatusIconSent.Glyph = glyph;
            StatusIconRead.Glyph = glyph;
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

        private void RefreshStripText()
        {
            RefreshAuthorRuns();
            RefreshPreviewRuns();
        }

        private void RefreshAuthorRuns()
        {
            if (AuthorBlock != null)
            {
                EmojiRichRuns.SetTextBlock(AuthorBlock, Author ?? string.Empty);
            }

            if (AuthorDetailBlock != null)
            {
                EmojiRichRuns.SetTextBlock(AuthorDetailBlock, AuthorDetail ?? string.Empty);
            }
        }

        private void RefreshPreviewRuns()
        {
            if (PreviewTextBlock == null)
            {
                return;
            }

            // Mentions stay plain (resolved names); emoji graphemes use EmojiFont like bubbles.
            EmojiRichRuns.SetTextBlock(
                PreviewTextBlock,
                CommentRichService.FormatMentionsPlain(
                    Text,
                    MentionedJids as IEnumerable<string>,
                    MentionLookup as IReadOnlyDictionary<string, string>));
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
            if (AuthorDetailBlock != null)
            {
                AuthorDetailBlock.ClearValue(FrameworkElement.MaxWidthProperty);
            }

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
            if (AuthorChevron != null)
            {
                AuthorChevron.Measure(infinite);
            }

            if (AuthorDetailBlock != null)
            {
                AuthorDetailBlock.Measure(infinite);
            }

            KindHost.Measure(infinite);
            PreviewTextBlock.Measure(infinite);

            // Inlines (emoji runs) leave TextBlock.Text empty — use the bound props.
            bool hasAuthor = AuthorBlock.Visibility == Visibility.Visible &&
                             !string.IsNullOrEmpty(Author);
            bool hasChevron = AuthorChevron != null &&
                              AuthorChevron.Visibility == Visibility.Visible;
            bool hasAuthorDetail = AuthorDetailBlock != null &&
                                   AuthorDetailBlock.Visibility == Visibility.Visible &&
                                   !string.IsNullOrEmpty(AuthorDetail);
            // GroupJoin is plain system text — no media chip (same budget as Text).
            bool hasChip = Kind != ChatPreviewKind.Text && Kind != ChatPreviewKind.GroupJoin;
            bool hasText = !string.IsNullOrEmpty(Text);

            double authorDesired = hasAuthor ? AuthorBlock.DesiredSize.Width : 0;
            double chevronDesired = hasChevron ? AuthorChevron.DesiredSize.Width : 0;
            double detailDesired = hasAuthorDetail ? AuthorDetailBlock.DesiredSize.Width : 0;
            double chipDesired = hasChip ? KindHost.DesiredSize.Width : 0;
            double leftover = System.Math.Max(0, available - statusWidth);

            double reserveForTrailing = 0;
            if (hasChip || hasText)
            {
                reserveForTrailing = MinTrailingWidth;
            }

            double leadingFixed = chevronDesired;
            double authorWidth = authorDesired;
            double detailWidth = detailDesired;
            if (hasAuthor || hasAuthorDetail)
            {
                double authorBudget = System.Math.Max(
                    0,
                    leftover - reserveForTrailing - leadingFixed);
                // Prefer keeping the group name readable; ellipsize sender detail first.
                if (hasAuthor && hasAuthorDetail)
                {
                    double authorMax = System.Math.Max(authorBudget * 0.55, 48);
                    authorWidth = System.Math.Min(authorDesired, authorMax);
                    detailWidth = System.Math.Min(
                        detailDesired,
                        System.Math.Max(0, authorBudget - authorWidth));
                    AuthorBlock.MaxWidth = authorWidth;
                    AuthorDetailBlock.MaxWidth = detailWidth;
                }
                else if (hasAuthor)
                {
                    authorWidth = System.Math.Min(authorDesired, authorBudget);
                    AuthorBlock.MaxWidth = authorWidth;
                }
                else
                {
                    detailWidth = System.Math.Min(detailDesired, authorBudget);
                    AuthorDetailBlock.MaxWidth = detailWidth;
                }
            }

            double remaining = System.Math.Max(
                0,
                leftover - authorWidth - leadingFixed - detailWidth);
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
