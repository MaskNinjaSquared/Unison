using System;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// WhatsApp-shell voice transport: transparent icon play/pause, label ↔ slider, overflow|timestamp+speed.
    /// </summary>
    public sealed partial class ChatAudioBubbleBar : UserControl
    {
        public static readonly DependencyProperty GlyphForegroundProperty =
            DependencyProperty.Register(
                nameof(GlyphForeground),
                typeof(Brush),
                typeof(ChatAudioBubbleBar),
                new PropertyMetadata(null, OnGlyphForegroundChanged));

        private static void OnGlyphForegroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as ChatAudioBubbleBar;
            bar?.ApplyLegacySliderForeground();
        }

        private ChatAudioTransportPresenter _presenter;

        public ChatAudioBubbleBar()
        {
            InitializeComponent();
            _presenter = new ChatAudioTransportPresenter(
                this,
                PlayButton,
                PauseButton,
                ReadyLabel,
                PositionSlider,
                SpeedButton,
                TimestampText,
                SpeedText,
                MoreButton,
                OnPlayPause,
                OnMore);
            DataContextChanged += ChatAudioBubbleBar_DataContextChanged;
            Unloaded += ChatAudioBubbleBar_Unloaded;
            Loaded += ChatAudioBubbleBar_Loaded;
        }

        public Brush GlyphForeground
        {
            get => (Brush)GetValue(GlyphForegroundProperty);
            set => SetValue(GlyphForegroundProperty, value);
        }

        /// <summary>Legacy: 2px ring, transparent fill, icon. WhatsApp keeps the bare glyph.</summary>
        public bool UseRing
        {
            get => PlayRing != null && PlayRing.Visibility == Visibility.Visible;
            set
            {
                if (PlayRing != null)
                {
                    PlayRing.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
                }

                ApplyLegacySliderForeground();
            }
        }

        private void ChatAudioBubbleBar_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyLegacyChrome();
            _presenter.Attach(DataContext as ChatMessageViewModel);
            _presenter.ApplyState(animate: false);
        }

        private void ApplyLegacyChrome()
        {
            if (!UseRing)
            {
                return;
            }

            Style hitStyle = TryStyle("MediaHitTargetButtonStyle");
            if (hitStyle != null)
            {
                PlayButton.Style = hitStyle;
                PauseButton.Style = hitStyle;
                MoreButton.Style = hitStyle;
                SpeedButton.Style = hitStyle;
            }

            ApplyLegacySliderForeground();

            FontFamily family = LegacyMediaGlyphs.IconFont;
            if (family == null)
            {
                return;
            }

            ApplyGlyph(PlayButton, LegacyMediaGlyphs.Play, family);
            ApplyGlyph(PauseButton, LegacyMediaGlyphs.Pause, family);
        }

        /// <summary>
        /// Accent green matches the dark sent bubble in Legacy; use bubble ink instead.
        /// </summary>
        private void ApplyLegacySliderForeground()
        {
            if (PositionSlider == null || !UseRing)
            {
                return;
            }

            Brush ink = GlyphForeground;
            if (ink != null)
            {
                PositionSlider.Foreground = ink;
            }
        }

        private static Style TryStyle(string key)
        {
            object styleObj;
            return Application.Current.Resources.TryGetValue(key, out styleObj)
                ? styleObj as Style
                : null;
        }

        private static void ApplyGlyph(Button button, string glyph, FontFamily family)
        {
            var label = button?.Content as TextBlock;
            if (label == null)
            {
                return;
            }

            label.FontFamily = family;
            label.Text = glyph;
            label.TextLineBounds = TextLineBounds.Tight;
        }

        private void ChatAudioBubbleBar_Unloaded(object sender, RoutedEventArgs e)
        {
            _presenter.Detach();
        }

        private void ChatAudioBubbleBar_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            _presenter.Attach(args.NewValue as ChatMessageViewModel);
            _presenter.ApplyState(animate: false);
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            _presenter.OnPlayPauseClick();
        }

        private void PlayButton_Holding(object sender, HoldingRoutedEventArgs e)
        {
            _presenter.OnPlayHolding(e);
        }

        private void Speed_Click(object sender, RoutedEventArgs e)
        {
            _presenter.OnSpeedClick();
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            _presenter.OnMoreClick();
        }

        private void PositionSlider_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _presenter.OnSliderPointerPressed();
        }

        private void PositionSlider_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _presenter.OnSliderPointerReleased();
        }

        private void PositionSlider_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _presenter.OnSliderPointerReleased();
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            _presenter.OnSliderValueChanged(e.NewValue);
        }

        private void OnPlayPause()
        {
            FindChatDetail()?.OnAudioPlayButtonClick(this, new RoutedEventArgs());
        }

        private void OnMore()
        {
            ChatAudioOverflowMenu.Show(MoreButton, DataContext as ChatMessageViewModel, OnPlayPause);
        }

        private static IChatDetailSurface FindChatDetail(DependencyObject start)
        {
            var current = start;
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

        private IChatDetailSurface FindChatDetail()
        {
            return FindChatDetail(this);
        }
    }
}
