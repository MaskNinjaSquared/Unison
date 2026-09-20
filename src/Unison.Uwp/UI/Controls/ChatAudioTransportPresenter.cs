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
    /// Shared play/pause ↔ label/slider ↔ timestamp state for shell-specific audio bars.
    /// </summary>
    internal sealed class ChatAudioTransportPresenter
    {
        private readonly FrameworkElement _owner;
        private readonly Button _playButton;
        private readonly Button _pauseButton;
        private readonly TextBlock _readyLabel;
        private readonly Slider _positionSlider;
        private readonly TextBlock _timestampText;
        private readonly Action _onPlayPause;

        private ChatMessageViewModel _vm;
        private bool _sliderDragging;
        private bool _suppressSliderCallback;
        private bool _showingSlider;
        private Storyboard _fadeStoryboard;

        public ChatAudioTransportPresenter(
            FrameworkElement owner,
            Button playButton,
            Button pauseButton,
            TextBlock readyLabel,
            Slider positionSlider,
            TextBlock timestampText,
            Action onPlayPause)
        {
            _owner = owner ?? throw new ArgumentNullException(nameof(owner));
            _playButton = playButton ?? throw new ArgumentNullException(nameof(playButton));
            _pauseButton = pauseButton ?? throw new ArgumentNullException(nameof(pauseButton));
            _readyLabel = readyLabel ?? throw new ArgumentNullException(nameof(readyLabel));
            _positionSlider = positionSlider ?? throw new ArgumentNullException(nameof(positionSlider));
            _timestampText = timestampText ?? throw new ArgumentNullException(nameof(timestampText));
            _onPlayPause = onPlayPause ?? throw new ArgumentNullException(nameof(onPlayPause));
        }

        public void Attach(ChatMessageViewModel vm)
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

        public void Detach()
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

        public void ApplyState(bool animate)
        {
            var vm = _vm;
            if (vm == null)
            {
                return;
            }

            bool showPlay = vm.ShowAudioPlayButton;
            bool showPause = vm.ShowAudioPauseButton;
            bool showSlider = vm.ShowAudioSlider;

            _playButton.Visibility = showPlay ? Visibility.Visible : Visibility.Collapsed;
            _pauseButton.Visibility = showPause ? Visibility.Visible : Visibility.Collapsed;
            _readyLabel.Text = vm.AudioReadyLabelText ?? string.Empty;
            _timestampText.Text = vm.AudioTimestampText ?? "0:00";

            _suppressSliderCallback = true;
            try
            {
                _positionSlider.Maximum = Math.Max(1, vm.AudioSliderMaximum);
                _positionSlider.Value = Math.Min(_positionSlider.Maximum, vm.AudioSliderValue);
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

        public void OnPlayPauseClick()
        {
            _onPlayPause();
        }

        public void OnSliderPointerPressed()
        {
            _sliderDragging = true;
        }

        public void OnSliderPointerReleased()
        {
            _sliderDragging = false;
            SyncSliderFromVm();
        }

        public void OnSliderValueChanged(double newValue)
        {
            if (_suppressSliderCallback || _vm == null)
            {
                return;
            }

            uint seconds = (uint)Math.Max(0, Math.Floor(newValue));
            _vm.AudioPlaybackPositionSeconds = seconds;
            _timestampText.Text = _vm.AudioTimestampText;
            FindChatDetail()?.SeekAudioPlayback(_vm, newValue);
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
                if (_vm != null)
                {
                    _timestampText.Text = _vm.AudioTimestampText;
                }
            }
        }

        private void SyncSliderFromVm()
        {
            if (_vm == null || _sliderDragging)
            {
                return;
            }

            _suppressSliderCallback = true;
            try
            {
                double max = Math.Max(1, _vm.AudioSliderMaximum);
                if (Math.Abs(_positionSlider.Maximum - max) > 0.01)
                {
                    _positionSlider.Maximum = max;
                }

                double value = Math.Min(max, _vm.AudioSliderValue);
                if (Math.Abs(_positionSlider.Value - value) >= 0.5)
                {
                    _positionSlider.Value = value;
                }
            }
            finally
            {
                _suppressSliderCallback = false;
            }
        }

        private void SetMidVisibility(bool showSlider)
        {
            _readyLabel.Opacity = showSlider ? 0 : 1;
            _readyLabel.IsHitTestVisible = !showSlider;
            _positionSlider.Opacity = showSlider ? 1 : 0;
            _positionSlider.IsHitTestVisible = showSlider;
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
            Storyboard.SetTarget(labelAnim, _readyLabel);
            Storyboard.SetTargetProperty(labelAnim, "Opacity");
            storyboard.Children.Add(labelAnim);

            var sliderAnim = new DoubleAnimation
            {
                To = toSlider ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            Storyboard.SetTarget(sliderAnim, _positionSlider);
            Storyboard.SetTargetProperty(sliderAnim, "Opacity");
            storyboard.Children.Add(sliderAnim);

            _readyLabel.IsHitTestVisible = !toSlider;
            _positionSlider.IsHitTestVisible = toSlider;
            _fadeStoryboard = storyboard;
            storyboard.Begin();
        }

        private IChatDetailSurface FindChatDetail()
        {
            DependencyObject current = _owner;
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
