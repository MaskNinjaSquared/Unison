using System.Collections.Generic;

using Unison.Core.Models;

using Unison.Uwp.UI.Helpers;

using Windows.Foundation;

using Windows.UI.Xaml;

using Windows.UI.Xaml.Controls;

using Windows.UI.Xaml.Media;



namespace Unison.Uwp.UI.Controls

{

    /// <summary>

    /// Chat-list subtitle: optional group author + media chip + preview text.

    /// Group composite rows prioritize the author (full when possible); then the

    /// kind chip / body share the remainder and ellipsize as continuous text.

    /// </summary>

    public sealed partial class ChatListPreviewStrip : UserControl

    {

        private const double MinTrailingWidth = 28;

        private long _foregroundToken = -1;



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



        public ChatListPreviewStrip()

        {

            InitializeComponent();

            _foregroundToken = RegisterPropertyChangedCallback(ForegroundProperty, OnForegroundPropertyChanged);

            Loaded += (_, __) =>

            {

                RefreshPreviewText();

                SyncKindHostForeground();

                Relayout();

            };

            Unloaded += (_, __) =>

            {

                if (_foregroundToken >= 0)

                {

                    UnregisterPropertyChangedCallback(ForegroundProperty, _foregroundToken);

                    _foregroundToken = -1;

                }

            };

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



        /// <summary>Optional mention JIDs for alias lookup (same as bubble <c>MentionedJids</c>).</summary>

        public object MentionedJids

        {

            get { return GetValue(MentionedJidsProperty); }

            set { SetValue(MentionedJidsProperty, value); }

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



            // ContentControl DataTemplates do not inherit Foreground; re-apply after Kind swap.

            if (strip.KindHost != null)

            {

                strip.KindHost.LayoutUpdated -= strip.KindHost_LayoutUpdated;

                strip.KindHost.LayoutUpdated += strip.KindHost_LayoutUpdated;

            }



            strip.Relayout();

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

                MentionedJids as IEnumerable<string>);

        }



        /// <summary>

        /// DataTemplate content under ContentControl does not inherit list-item

        /// selection Foreground; push Root.Foreground onto chip glyphs/labels

        /// (voice keeps its own accent brushes).

        /// </summary>

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



        /// <summary>

        /// Distribute width: author preferred intact; chip then body take leftovers and ellipsize.

        /// Long authors still truncate (no hard MaxWidth=96 cap).

        /// </summary>

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

            AuthorBlock.Measure(infinite);

            KindHost.Measure(infinite);

            PreviewTextBlock.Measure(infinite);



            bool hasAuthor = AuthorBlock.Visibility == Visibility.Visible &&

                             !string.IsNullOrEmpty(AuthorBlock.Text);

            bool hasChip = Kind != ChatPreviewKind.Text;

            bool hasText = !string.IsNullOrEmpty(PreviewTextBlock.Text);



            double authorDesired = hasAuthor ? AuthorBlock.DesiredSize.Width : 0;

            double chipDesired = hasChip ? KindHost.DesiredSize.Width : 0;



            double reserveForTrailing = 0;

            if (hasChip || hasText)

            {

                reserveForTrailing = MinTrailingWidth;

            }



            double authorWidth = authorDesired;

            if (hasAuthor)

            {

                double authorMax = System.Math.Max(0, available - reserveForTrailing);

                authorWidth = System.Math.Min(authorDesired, authorMax);

                AuthorBlock.MaxWidth = authorWidth;

            }



            double remaining = System.Math.Max(0, available - authorWidth);



            if (hasChip && hasText)

            {

                double chipWidth = System.Math.Min(chipDesired, remaining);

                // Prefer a scrap of body when both need space; chip still ellipsizes first.

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


