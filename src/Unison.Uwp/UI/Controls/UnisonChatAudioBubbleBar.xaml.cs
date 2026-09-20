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
    /// Unison-shell voice transport: round brand play/pause, label ↔ slider, timestamp.
    /// </summary>
    public sealed partial class UnisonChatAudioBubbleBar : UserControl
    {
        public static readonly DependencyProperty IsFromMeProperty =
            DependencyProperty.Register(
                nameof(IsFromMe),
                typeof(bool),
                typeof(UnisonChatAudioBubbleBar),
                new PropertyMetadata(false, OnIsFromMeChanged));

        private ChatAudioTransportPresenter _presenter;

        public UnisonChatAudioBubbleBar()
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
            DataContextChanged += UnisonChatAudioBubbleBar_DataContextChanged;
            Unloaded += UnisonChatAudioBubbleBar_Unloaded;
            Loaded += UnisonChatAudioBubbleBar_Loaded;
        }

        public bool IsFromMe
        {
            get => (bool)GetValue(IsFromMeProperty);
            set => SetValue(IsFromMeProperty, value);
        }

        private static void OnIsFromMeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as UnisonChatAudioBubbleBar;
            bar?.ApplyDirectionState();
        }

        private void ApplyDirectionState()
        {
            string state = IsFromMe ? "Sent" : "Received";
            VisualStateManager.GoToState(this, state, false);
        }

        private void UnisonChatAudioBubbleBar_Loaded(object sender, RoutedEventArgs e)
        {
            ApplyDirectionState();
            _presenter.Attach(DataContext as ChatMessageViewModel);
            _presenter.ApplyState(animate: false);
        }

        private void UnisonChatAudioBubbleBar_Unloaded(object sender, RoutedEventArgs e)
        {
            _presenter.Detach();
        }

        private void UnisonChatAudioBubbleBar_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
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

        private IChatDetailSurface FindChatDetail()
        {
            DependencyObject current = this;
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
