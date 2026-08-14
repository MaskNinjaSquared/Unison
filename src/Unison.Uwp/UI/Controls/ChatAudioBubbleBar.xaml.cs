using System;
using System.ComponentModel;
using Unison.Core.Models;
using Unison.Core.ViewModels;
using Unison.Uwp.UI.Views;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Controls
{
    /// <summary>
    /// Voice bubble transport: play/pause — Ready label or Imgur-style slider — timestamp.
    /// Slider only while <see cref="AudioPlaybackStatus.Playing"/> / <see cref="AudioPlaybackStatus.Paused"/>,
    /// with a short fade when swapping label ↔ slider. Min width 250.
    /// </summary>
    public sealed partial class ChatAudioBubbleBar : UserControl
    {
        public static readonly DependencyProperty GlyphForegroundProperty =
            DependencyProperty.Register(
                nameof(GlyphForeground),
                typeof(Brush),
                typeof(ChatAudioBubbleBar),
                new PropertyMetadata(null));

        private ChatMessageViewModel _vm;
        private bool _sliderDragging;
        private bool _suppressSliderCallback;
        private bool _showingSlider;
        private Storyboard _fadeStoryboard;

        public ChatAudioBubbleBar()
        {
            InitializeComponent();
            DataContextChanged += ChatAudioBubbleBar_DataContextChanged;
            Unloaded += ChatAudioBubbleBar_Unloaded;
            Loaded += ChatAudioBubbleBar_Loaded;
        }

        public Brush GlyphForeground
        {
            get => (Brush)GetValue(GlyphForegroundProperty);
            set => SetValue(GlyphForegroundProperty, value);
        }

        private ChatMessageViewModel ViewModel => _vm ?? DataContext as ChatMessageViewModel;

        private void ChatAudioBubbleBar_Loaded(object sender, RoutedEventArgs e)
        {
            Attach(DataContext as ChatMessageViewModel);
            ApplyState(animate: false);
        }

        private void ChatAudioBubbleBar_Unloaded(object sender, RoutedEventArgs e)
        {
            Detach();
        }

        private void ChatAudioBubbleBar_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            Attach(args.NewValue as ChatMessageViewModel);
            ApplyState(animate: false);
        }

        private void Attach(ChatMessageViewModel vm)
        {
            if (ReferenceEquals(_vm, vm))
            {
                return;
            }

            Detach();
            _vm = vm;
            if (_vm != null)
            {
                _vm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void Detach()
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= ViewModel_PropertyChanged;
                _vm = null;
            }

            try
            {
                _fadeStoryboard?.Stop();
            }
            catch
            {
            }
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ChatMessageViewModel.AudioPlaybackStatus) ||
                e.PropertyName == nameof(ChatMessageViewModel.ShowAudioPlayButton) ||
                e.PropertyName == nameof(ChatMessageViewModel.ShowAudioPauseButton) ||
                e.PropertyName == nameof(ChatMessageViewModel.AudioReadyLabelText) ||
                e.PropertyName == nameof(ChatMessageViewModel.AudioTimestampText) ||
                e.PropertyName == nameof(ChatMessageViewModel.AudioSliderMaximum))
            {
                ApplyState(animate: e.PropertyName == nameof(ChatMessageViewModel.AudioPlaybackStatus));
            }
            else if (e.PropertyName == nameof(ChatMessageViewModel.AudioPlaybackPositionSeconds) ||
                     e.PropertyName == nameof(ChatMessageViewModel.AudioSliderValue))
            {
                SyncSliderFromVm();
                if (TimestampText != null && ViewModel != null)
                {
                    TimestampText.Text = ViewModel.AudioTimestampText;
                }
            }
        }

        private void ApplyState(bool animate)
        {
            var vm = ViewModel;
            if (vm == null)
            {
                return;
            }

            bool showPlay = vm.ShowAudioPlayButton;
            bool showPause = vm.ShowAudioPauseButton;
            bool showSlider = vm.ShowAudioSlider;

            PlayButton.Visibility = showPlay ? Visibility.Visible : Visibility.Collapsed;
            PauseButton.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
            ReadyLabel.Text = vm.AudioReadyLabelText ?? string.Empty;
            TimestampText.Text = vm.AudioTimestampText ?? "0:00";

            _suppressSliderCallback = true;
            try
            {
                PositionSlider.Maximum = Math.Max(1, vm.AudioSliderMaximum);
                PositionSlider.Value = Math.Min(PositionSlider.Maximum, vm.AudioSliderValue);
            }
            finally
            {
                _suppressSliderCallback = false;
            }

            if (showSlider != _showingSlider)
            {
                if (animate)
                {
                    FadeSwap(showSlider);
                }
                else
                {
                    SetMidVisibility(showSlider);
                }

                _showingSlider = showSlider;
            }
            else if (!animate)
            {
                SetMidVisibility(showSlider);
            }
        }

        private void SyncSliderFromVm()
        {
            var vm = ViewModel;
            if (vm == null || _sliderDragging || PositionSlider == null)
            {
                return;
            }

            _suppressSliderCallback = true;
            try
            {
                double max = Math.Max(1, vm.AudioSliderMaximum);
                if (Math.Abs(PositionSlider.Maximum - max) > 0.01)
                {
                    PositionSlider.Maximum = max;
                }

                double value = Math.Min(max, vm.AudioSliderValue);
                if (Math.Abs(PositionSlider.Value - value) >= 0.5)
                {
                    PositionSlider.Value = value;
                }
            }
            finally
            {
                _suppressSliderCallback = false;
            }
        }

        private void SetMidVisibility(bool showSlider)
        {
            ReadyLabel.Opacity = showSlider ? 0 : 1;
            ReadyLabel.IsHitTestVisible = !showSlider;
            PositionSlider.Opacity = showSlider ? 1 : 0;
            PositionSlider.IsHitTestVisible = showSlider;
        }

        private void FadeSwap(bool toSlider)
        {
            try
            {
                _fadeStoryboard?.Stop();
            }
            catch
            {
            }

            var storyboard = new Storyboard();
            var labelAnim = new DoubleAnimation
            {
                To = toSlider ? 0 : 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(labelAnim, ReadyLabel);
            Storyboard.SetTargetProperty(labelAnim, "Opacity");
            storyboard.Children.Add(labelAnim);

            var sliderAnim = new DoubleAnimation
            {
                To = toSlider ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(sliderAnim, PositionSlider);
            Storyboard.SetTargetProperty(sliderAnim, "Opacity");
            storyboard.Children.Add(sliderAnim);

            ReadyLabel.IsHitTestVisible = !toSlider;
            PositionSlider.IsHitTestVisible = toSlider;
            _fadeStoryboard = storyboard;
            storyboard.Begin();
        }

        private void PlayPause_Click(object sender, RoutedEventArgs e)
        {
            FindChatDetail()?.OnAudioPlayButtonClick(this, e);
        }

        private void PositionSlider_PointerPressed(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sliderDragging = true;
        }

        private void PositionSlider_PointerReleased(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sliderDragging = false;
            SyncSliderFromVm();
        }

        private void PositionSlider_PointerCaptureLost(object sender, Windows.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            _sliderDragging = false;
            SyncSliderFromVm();
        }

        private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderCallback || ViewModel == null)
            {
                return;
            }

            // Interactive scrub (timer / ApplyState always suppress before writing Value).
            uint seconds = (uint)Math.Max(0, Math.Floor(e.NewValue));
            ViewModel.AudioPlaybackPositionSeconds = seconds;
            if (TimestampText != null)
            {
                TimestampText.Text = ViewModel.AudioTimestampText;
            }

            FindChatDetail()?.SeekAudioPlayback(ViewModel, e.NewValue);
        }

        private static ChatDetailView FindChatDetail(DependencyObject start)
        {
            var current = start;
            while (current != null)
            {
                var view = current as ChatDetailView;
                if (view != null)
                {
                    return view;
                }

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private ChatDetailView FindChatDetail()
        {
            return FindChatDetail(this);
        }
    }
}
