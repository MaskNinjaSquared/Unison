using System;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// WhatsApp-shell voice transport: transparent icon play/pause, label ↔ slider, timestamp.
    /// </summary>
    public sealed partial class ChatAudioBubbleBar : UserControl
    {
        public static readonly DependencyProperty GlyphForegroundProperty =
            DependencyProperty.Register(
                nameof(GlyphForeground),
                typeof(Brush),
                typeof(ChatAudioBubbleBar),
                new PropertyMetadata(null));

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
                TimestampText,
                OnPlayPause);
            DataContextChanged += ChatAudioBubbleBar_DataContextChanged;
            Unloaded += ChatAudioBubbleBar_Unloaded;
            Loaded += ChatAudioBubbleBar_Loaded;
        }

        public Brush GlyphForeground
        {
            get => (Brush)GetValue(GlyphForegroundProperty);
            set => SetValue(GlyphForegroundProperty, value);
        }

        private void ChatAudioBubbleBar_Loaded(object sender, RoutedEventArgs e)
        {
            _presenter.Attach(DataContext as ChatMessageViewModel);
            _presenter.ApplyState(animate: false);
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

        private void PositionSlider_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _presenter.OnSliderPointerPressed();
        }

        private void PositionSlider_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _presenter.OnSliderPointerReleased();
        }

        private void PositionSlider_PointerCaptureLost(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
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
