using System;
using System.ComponentModel;
using System.Diagnostics;
using Unison.Core.ViewModels;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Controls.Primitives;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using Windows.UI.Xaml.Media.Animation;

namespace Unison.Uwp.UI.Views
{
    /// <summary>
    /// Full-screen video overlay (Imgur <c>FullScreenMediaView</c> layout + gestures).
    /// SMTC metadata is applied only while this overlay is open.
    /// </summary>
    public sealed partial class VideoViewerView : UserControl
    {
        private readonly DispatcherTimer _positionTimer;
        private readonly DispatcherTimer _chromeTimer;
        private Storyboard _seekFeedbackStoryboard;
        private VideoViewerViewModel _viewModel;
        private MediaPlayer _mediaPlayer;
        private bool _suppressSliderCallback;
        private const int SeekSeconds = 10;
        private static readonly object ClosedDataContext = new object();

        public event EventHandler CloseRequested;

        /// <summary>
        /// Optional hook so the host can supply SMTC title/artist (group vs DM).
        /// </summary>
        public Func<ChatMessageViewModel, Tuple<string, string>> ResolveSmtcMetadata { get; set; }

        public VideoViewerView()
        {
            InitializeComponent();
            DataContext = ClosedDataContext;
            Unloaded += VideoViewerView_Unloaded;

            _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _positionTimer.Tick += PositionTimer_Tick;

            _chromeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _chromeTimer.Tick += (s, e) =>
            {
                HideChrome();
                _chromeTimer.Stop();
            };
        }

        public VideoViewerViewModel ViewModel
        {
            get => _viewModel;
            set
            {
                if (ReferenceEquals(_viewModel, value))
                {
                    return;
                }

                if (_viewModel != null)
                {
                    _viewModel.CloseRequested -= ViewModel_CloseRequested;
                    _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                }

                TearDownPlayer();
                _viewModel = value;
                // Avoid inheriting ChatDetailViewModel while overlay is closed.
                DataContext = (object)value ?? ClosedDataContext;
                Bindings.Update();

                if (_viewModel != null)
                {
                    _viewModel.CloseRequested += ViewModel_CloseRequested;
                    _viewModel.PropertyChanged += ViewModel_PropertyChanged;
                    ApplyChromeVisibility(_viewModel.IsChromeVisible);
                    StartPlayback(_viewModel);
                    ShowChrome();
                }
            }
        }

        private void VideoViewerView_Unloaded(object sender, RoutedEventArgs e)
        {
            ViewModel = null;
        }

        private void ViewModel_CloseRequested(object sender, EventArgs e)
        {
            TearDownPlayer();
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        private void ViewModel_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(VideoViewerViewModel.IsChromeVisible) && _viewModel != null)
            {
                ApplyChromeVisibility(_viewModel.IsChromeVisible);
            }
        }

        private void ApplyChromeVisibility(bool visible)
        {
            if (visible)
            {
                ShowChrome();
            }
            else
            {
                HideChrome();
            }
        }

        private void StartPlayback(VideoViewerViewModel vm)
        {
            if (vm == null || string.IsNullOrWhiteSpace(vm.VideoUri))
            {
                return;
            }

            try
            {
                EnsureMediaPlayer();
                // Source assignment clears DisplayUpdater — apply again in MediaOpened.
                _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(vm.VideoUri));
                _mediaPlayer.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VideoViewerView] StartPlayback: " + ex.Message);
            }
        }

        private void EnsureMediaPlayer()
        {
            if (_mediaPlayer != null)
            {
                return;
            }

            _mediaPlayer = new MediaPlayer
            {
                AutoPlay = false,
                AudioCategory = MediaPlayerAudioCategory.Media
            };
            _mediaPlayer.MediaOpened += MediaPlayer_MediaOpened;
            _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;
            _mediaPlayer.PlaybackSession.PlaybackStateChanged += PlaybackSession_PlaybackStateChanged;
            _mediaPlayer.CommandManager.IsEnabled = true;
            Player.SetMediaPlayer(_mediaPlayer);
        }

        private void TearDownPlayer()
        {
            try
            {
                _positionTimer.Stop();
                _chromeTimer.Stop();
            }
            catch
            {
            }

            if (_mediaPlayer == null)
            {
                return;
            }

            try
            {
                _mediaPlayer.MediaOpened -= MediaPlayer_MediaOpened;
                _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
                _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
                _mediaPlayer.PlaybackSession.PlaybackStateChanged -= PlaybackSession_PlaybackStateChanged;
                _mediaPlayer.Pause();
                _mediaPlayer.Source = null;
                try
                {
                    _mediaPlayer.SystemMediaTransportControls.IsEnabled = false;
                }
                catch
                {
                }
            }
            catch
            {
            }

            try
            {
                Player.SetMediaPlayer(null);
            }
            catch
            {
            }

            try
            {
                _mediaPlayer.Dispose();
            }
            catch
            {
            }

            _mediaPlayer = null;
        }

        private void ApplySmtcMetadata(VideoViewerViewModel vm)
        {
            try
            {
                if (_mediaPlayer == null || vm?.Message == null)
                {
                    return;
                }

                string title = "Unison";
                string artist = "Video";
                var resolver = ResolveSmtcMetadata;
                if (resolver != null)
                {
                    var meta = resolver(vm.Message);
                    if (meta != null)
                    {
                        if (!string.IsNullOrWhiteSpace(meta.Item1))
                        {
                            title = meta.Item1.Trim();
                        }

                        if (!string.IsNullOrWhiteSpace(meta.Item2))
                        {
                            artist = meta.Item2.Trim();
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(vm.SenderDisplayName))
                {
                    artist = vm.SenderDisplayName.Trim();
                }

                var smtc = _mediaPlayer.SystemMediaTransportControls;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;

                var updater = smtc.DisplayUpdater;
                updater.ClearAll();
                // Music display mirrors voice bubbles (title/artist) so group vs DM rules stay consistent.
                updater.Type = MediaPlaybackType.Music;
                updater.AppMediaId = "Unison.Video";
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = artist;
                updater.Update();

                Debug.WriteLine(
                    "[VideoViewerView] SMTC title=\"" + title + "\" artist=\"" + artist + "\"");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[VideoViewerView] SMTC: " + ex.Message);
            }
        }

        private async void MediaPlayer_MediaOpened(MediaPlayer sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    // Source assignment clears DisplayUpdater — re-apply after open (same as voice).
                    ApplySmtcMetadata(_viewModel);

                    _suppressSliderCallback = true;
                    try
                    {
                        PlayerSlider.Minimum = 0;
                        double max = sender.PlaybackSession.NaturalDuration.TotalSeconds;
                        PlayerSlider.Maximum = max > 0 && !double.IsNaN(max) ? max : 1;
                        PlayerSlider.Value = 0;
                    }
                    finally
                    {
                        _suppressSliderCallback = false;
                    }
                });
            }
            catch
            {
            }
        }

        private async void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    canPlayIcon.Visibility = Visibility.Visible;
                    canPauseIcon.Visibility = Visibility.Collapsed;
                    _positionTimer.Stop();
                    ShowChrome();
                });
            }
            catch
            {
            }
        }

        private async void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            Debug.WriteLine("[VideoViewerView] MediaFailed: " + args?.ErrorMessage);
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    canPlayIcon.Visibility = Visibility.Visible;
                    canPauseIcon.Visibility = Visibility.Collapsed;
                    _positionTimer.Stop();
                });
            }
            catch
            {
            }
        }

        private async void PlaybackSession_PlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                {
                    switch (sender.PlaybackState)
                    {
                        case MediaPlaybackState.Playing:
                            canPauseIcon.Visibility = Visibility.Visible;
                            canPlayIcon.Visibility = Visibility.Collapsed;
                            _positionTimer.Start();
                            break;
                        case MediaPlaybackState.Paused:
                            canPlayIcon.Visibility = Visibility.Visible;
                            canPauseIcon.Visibility = Visibility.Collapsed;
                            _positionTimer.Stop();
                            break;
                    }
                });
            }
            catch
            {
            }
        }

        private void PositionTimer_Tick(object sender, object e)
        {
            try
            {
                if (_mediaPlayer?.PlaybackSession == null)
                {
                    return;
                }

                var session = _mediaPlayer.PlaybackSession;
                PositionText.Text = FormatClock(session.Position);
                _suppressSliderCallback = true;
                try
                {
                    PlayerSlider.Value = session.Position.TotalSeconds;
                }
                finally
                {
                    _suppressSliderCallback = false;
                }
            }
            catch
            {
            }
        }

        private static string FormatClock(TimeSpan t)
        {
            if (t.TotalHours >= 1)
            {
                return t.ToString(@"h\:mm\:ss");
            }

            return t.ToString(@"mm\:ss");
        }

        private void Player_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowChrome();
            if (_viewModel != null)
            {
                _viewModel.IsChromeVisible = true;
            }
        }

        private void Chrome_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            ShowChrome();
        }

        private void Root_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowChrome();

        private void Root_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _chromeTimer.Stop();
            _chromeTimer.Start();
        }

        private void ShowChrome()
        {
            HeaderChrome.Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x1C, 0x1C, 0x1C));
            HeaderChrome.Opacity = PlayerTransportControls.Opacity;
            PlayerTransportControls.Visibility = Visibility.Visible;
            TitleInfoPanel.Visibility = Visibility.Visible;
            _chromeTimer.Stop();
            _chromeTimer.Start();
        }

        private void HideChrome()
        {
            HeaderChrome.Background = new SolidColorBrush(Colors.Transparent);
            HeaderChrome.Opacity = 1;
            TitleInfoPanel.Visibility = Visibility.Collapsed;
            PlayerTransportControls.Visibility = Visibility.Collapsed;
            if (_viewModel != null)
            {
                _viewModel.IsChromeVisible = false;
            }
        }

        private void TransportControlPlay_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            if (_mediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
            {
                _mediaPlayer.Pause();
            }
            else
            {
                _mediaPlayer.Play();
            }

            ShowChrome();
        }

        private void PlayerSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressSliderCallback || _mediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            _mediaPlayer.PlaybackSession.Position = TimeSpan.FromSeconds(e.NewValue);
            PositionText.Text = FormatClock(_mediaPlayer.PlaybackSession.Position);
        }

        private void Next_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            _mediaPlayer.PlaybackSession.Position += TimeSpan.FromSeconds(SeekSeconds);
            ShowSeekFeedback(forward: true);
            ShowChrome();
        }

        private void Back_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            e.Handled = true;
            if (_mediaPlayer?.PlaybackSession == null)
            {
                return;
            }

            var pos = _mediaPlayer.PlaybackSession.Position - TimeSpan.FromSeconds(SeekSeconds);
            _mediaPlayer.PlaybackSession.Position = pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
            ShowSeekFeedback(forward: false);
            ShowChrome();
        }

        private void ShowSeekFeedback(bool forward)
        {
            var target = forward ? SeekForwardFeedback : SeekBackwardFeedback;
            var other = forward ? SeekBackwardFeedback : SeekForwardFeedback;
            other.Opacity = 0;
            try
            {
                _seekFeedbackStoryboard?.Stop();
            }
            catch
            {
            }

            var storyboard = new Storyboard();
            var animation = new DoubleAnimationUsingKeyFrames();
            animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(0), Value = 0 });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(120), Value = 1 });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(820), Value = 1 });
            animation.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromMilliseconds(1270), Value = 0 });
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, "Opacity");
            storyboard.Children.Add(animation);
            _seekFeedbackStoryboard = storyboard;
            storyboard.Begin();
        }
    }
}
