// =============================================================================
// ChatAudioPlaybackService
//
// Owns the single MediaPlayer for voice/audio bubbles. ViewModels orchestrate;
// views only AttachUi(MediaPlayerElement).
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.UI.Core;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;

namespace Unison.Uwp.Services
{
    /// <summary>UWP implementation of <see cref="IChatAudioPlaybackService"/>.</summary>
    public sealed class ChatAudioPlaybackService : IChatAudioPlaybackService
    {
        private readonly IVoicePlaybackRoutingService _routing;
        private readonly object _gate = new object();

        private MediaPlayer _player;
        private MediaPlayerElement _element;
        private DispatcherTimer _positionTimer;
        private CoreDispatcher _dispatcher;
        private AudioPlaybackSpeed _speed = AudioPlaybackSpeed.Regular;
        private string _smtcTitle = "Unison";
        private string _smtcArtist = string.Empty;
        private bool _isPlaying;

        public ChatAudioPlaybackService(IVoicePlaybackRoutingService routing)
        {
            _routing = routing ?? throw new ArgumentNullException(nameof(routing));
        }

        public event EventHandler<ChatAudioPlaybackStateChangedEventArgs> StateChanged;

        public AudioPlaybackSpeed Speed
        {
            get
            {
                lock (_gate)
                {
                    return _speed;
                }
            }
        }

        public bool IsPlaying
        {
            get
            {
                lock (_gate)
                {
                    return _isPlaying;
                }
            }
        }

        public void AttachUi(object mediaPlayerElement)
        {
            var element = mediaPlayerElement as MediaPlayerElement;
            lock (_gate)
            {
                _element = element;
                if (_element != null && _player != null)
                {
                    _element.SetMediaPlayer(_player);
                }

                try
                {
                    _dispatcher = Window.Current?.Dispatcher;
                }
                catch
                {
                    _dispatcher = null;
                }
            }

            EnsurePlayer();
        }

        public void DetachUi()
        {
            Stop();
            lock (_gate)
            {
                if (_element != null && _player != null)
                {
                    try
                    {
                        _element.SetMediaPlayer(null);
                    }
                    catch
                    {
                    }
                }

                _element = null;
            }
        }

        public void SetDisplayMetadata(string title, string artist)
        {
            lock (_gate)
            {
                _smtcTitle = string.IsNullOrWhiteSpace(title) ? "Unison" : title.Trim();
                _smtcArtist = artist ?? string.Empty;
            }

            ApplySmtcMetadata();
        }

        public Task PlayAsync(Uri source, AudioPlaybackSpeed speed)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            EnsurePlayer();
            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                _speed = speed;
            }

            if (player == null)
            {
                RaiseState(ChatAudioPlaybackTransportStatus.Failed, 0, speed, "no-player");
                return Task.CompletedTask;
            }

            try
            {
                ApplySpeedUnlocked(player, speed);
                player.Source = MediaSource.CreateFromUri(source);
                ApplySmtcMetadata();
                player.Play();
                lock (_gate)
                {
                    _isPlaying = true;
                }

                _routing.BeginSession();
                StartPositionTimer();
                RaiseState(ChatAudioPlaybackTransportStatus.Playing, 0, speed, null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] PlayAsync failed: " + ex.Message);
                RaiseState(ChatAudioPlaybackTransportStatus.Failed, 0, speed, ex.Message);
            }

            return Task.CompletedTask;
        }

        public void Pause()
        {
            MediaPlayer player;
            AudioPlaybackSpeed speed;
            lock (_gate)
            {
                player = _player;
                speed = _speed;
            }

            if (player == null)
            {
                return;
            }

            try
            {
                player.Pause();
                lock (_gate)
                {
                    _isPlaying = false;
                }

                StopPositionTimer();
                _routing.EndSession();
                RaiseState(
                    ChatAudioPlaybackTransportStatus.Paused,
                    ReadPositionSeconds(player),
                    speed,
                    null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] Pause failed: " + ex.Message);
            }
        }

        public void Resume()
        {
            MediaPlayer player;
            AudioPlaybackSpeed speed;
            lock (_gate)
            {
                player = _player;
                speed = _speed;
            }

            if (player == null)
            {
                return;
            }

            try
            {
                ApplySpeedUnlocked(player, speed);
                ApplySmtcMetadata();
                player.Play();
                lock (_gate)
                {
                    _isPlaying = true;
                }

                _routing.BeginSession();
                StartPositionTimer();
                RaiseState(
                    ChatAudioPlaybackTransportStatus.Playing,
                    ReadPositionSeconds(player),
                    speed,
                    null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] Resume failed: " + ex.Message);
            }
        }

        public void Stop()
        {
            MediaPlayer player;
            lock (_gate)
            {
                player = _player;
                _speed = AudioPlaybackSpeed.Regular;
                _isPlaying = false;
            }

            StopPositionTimer();
            _routing.EndSession();

            if (player != null)
            {
                try
                {
                    player.Pause();
                    player.Source = null;
                    ApplySpeedUnlocked(player, AudioPlaybackSpeed.Regular);
                }
                catch
                {
                }
            }

            RaiseState(
                ChatAudioPlaybackTransportStatus.Stopped,
                0,
                AudioPlaybackSpeed.Regular,
                null);
        }

        public void Seek(TimeSpan position)
        {
            MediaPlayer player;
            AudioPlaybackSpeed speed;
            bool playing;
            lock (_gate)
            {
                player = _player;
                speed = _speed;
                playing = _isPlaying;
            }

            if (player?.PlaybackSession == null)
            {
                return;
            }

            try
            {
                double seconds = position.TotalSeconds;
                if (seconds < 0)
                {
                    seconds = 0;
                }

                double max = player.PlaybackSession.NaturalDuration.TotalSeconds;
                if (!double.IsNaN(max) && max > 0 && seconds > max)
                {
                    seconds = max;
                }

                player.PlaybackSession.Position = TimeSpan.FromSeconds(seconds);
                RaiseState(
                    playing
                        ? ChatAudioPlaybackTransportStatus.Playing
                        : ChatAudioPlaybackTransportStatus.Paused,
                    (uint)Math.Floor(seconds),
                    speed,
                    null);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] Seek failed: " + ex.Message);
            }
        }

        public void SetSpeed(AudioPlaybackSpeed speed)
        {
            MediaPlayer player;
            lock (_gate)
            {
                _speed = speed;
                player = _player;
            }

            if (player != null)
            {
                ApplySpeedUnlocked(player, speed);
            }

            RaiseState(
                IsPlaying
                    ? ChatAudioPlaybackTransportStatus.Playing
                    : ChatAudioPlaybackTransportStatus.Paused,
                player != null ? ReadPositionSeconds(player) : 0,
                speed,
                null);
        }

        private void EnsurePlayer()
        {
            lock (_gate)
            {
                if (_player != null)
                {
                    return;
                }

                bool useCommunications = string.Equals(
                    _routing.PreferredAudioCategory,
                    "Communications",
                    StringComparison.OrdinalIgnoreCase);

                _player = new MediaPlayer
                {
                    AudioCategory = useCommunications
                        ? MediaPlayerAudioCategory.Communications
                        : MediaPlayerAudioCategory.Media,
                    AutoPlay = false
                };
                _player.MediaEnded += OnMediaEnded;
                _player.MediaFailed += OnMediaFailed;
                _player.MediaOpened += OnMediaOpened;
                _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
                _player.CommandManager.IsEnabled = true;

                if (_element != null)
                {
                    _element.SetMediaPlayer(_player);
                }

                _routing.AttachPlayer(_player);
            }
        }

        private static void ApplySpeedUnlocked(MediaPlayer player, AudioPlaybackSpeed speed)
        {
            try
            {
                if (player?.PlaybackSession != null)
                {
                    player.PlaybackSession.PlaybackRate = speed.ToRate();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] SetSpeed failed: " + ex.Message);
            }
        }

        private void ApplySmtcMetadata()
        {
            MediaPlayer player;
            string title;
            string artist;
            lock (_gate)
            {
                player = _player;
                title = _smtcTitle;
                artist = _smtcArtist;
            }

            if (player == null)
            {
                return;
            }

            try
            {
                var smtc = player.SystemMediaTransportControls;
                smtc.IsEnabled = true;
                smtc.IsPlayEnabled = true;
                smtc.IsPauseEnabled = true;

                var updater = smtc.DisplayUpdater;
                updater.ClearAll();
                updater.Type = MediaPlaybackType.Music;
                updater.AppMediaId = "Unison.VoiceNote";
                updater.MusicProperties.Title = title;
                updater.MusicProperties.Artist = artist;
                updater.Update();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] SMTC failed: " + ex.Message);
            }
        }

        private void StartPositionTimer()
        {
            if (_positionTimer == null)
            {
                _positionTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _positionTimer.Tick += OnPositionTick;
            }

            _positionTimer.Start();
        }

        private void StopPositionTimer()
        {
            try
            {
                _positionTimer?.Stop();
            }
            catch
            {
            }
        }

        private void OnPositionTick(object sender, object e)
        {
            MediaPlayer player;
            AudioPlaybackSpeed speed;
            bool playing;
            lock (_gate)
            {
                player = _player;
                speed = _speed;
                playing = _isPlaying;
            }

            if (!playing || player == null)
            {
                return;
            }

            RaiseState(
                ChatAudioPlaybackTransportStatus.Playing,
                ReadPositionSeconds(player),
                speed,
                null);
        }

        private async void OnMediaEnded(MediaPlayer sender, object args)
        {
            try
            {
                await RunOnUiAsync(() =>
                {
                    StopPositionTimer();
                    _routing.EndSession();
                    lock (_gate)
                    {
                        _isPlaying = false;
                        _speed = AudioPlaybackSpeed.Regular;
                    }

                    ApplySpeedUnlocked(sender, AudioPlaybackSpeed.Regular);
                    RaiseState(
                        ChatAudioPlaybackTransportStatus.Stopped,
                        0,
                        AudioPlaybackSpeed.Regular,
                        null);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] MediaEnded failed: " + ex.Message);
            }
        }

        private async void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            string message = args?.ErrorMessage ?? "media-failed";
            try
            {
                await RunOnUiAsync(() =>
                {
                    StopPositionTimer();
                    _routing.EndSession();
                    lock (_gate)
                    {
                        _isPlaying = false;
                        _speed = AudioPlaybackSpeed.Regular;
                    }

                    RaiseState(
                        ChatAudioPlaybackTransportStatus.Failed,
                        0,
                        AudioPlaybackSpeed.Regular,
                        message);
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatAudioPlayback] MediaFailed handler failed: " + ex.Message);
            }
        }

        private async void OnMediaOpened(MediaPlayer sender, object args)
        {
            try
            {
                await RunOnUiAsync(() =>
                {
                    ApplySmtcMetadata();
                    AudioPlaybackSpeed speed;
                    bool playing;
                    lock (_gate)
                    {
                        speed = _speed;
                        playing = _isPlaying;
                    }

                    ApplySpeedUnlocked(sender, speed);
                    if (playing)
                    {
                        _routing.BeginSession();
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async void OnPlaybackStateChanged(MediaPlaybackSession sender, object args)
        {
            try
            {
                await RunOnUiAsync(() =>
                {
                    MediaPlayer player;
                    AudioPlaybackSpeed speed;
                    lock (_gate)
                    {
                        player = _player;
                        speed = _speed;
                    }

                    if (player == null || sender == null)
                    {
                        return;
                    }

                    var state = sender.PlaybackState;
                    if (state == MediaPlaybackState.Paused)
                    {
                        lock (_gate)
                        {
                            _isPlaying = false;
                        }

                        StopPositionTimer();
                        RaiseState(
                            ChatAudioPlaybackTransportStatus.Paused,
                            ReadPositionSeconds(player),
                            speed,
                            null);
                    }
                    else if (state == MediaPlaybackState.Playing)
                    {
                        lock (_gate)
                        {
                            _isPlaying = true;
                        }

                        StartPositionTimer();
                        RaiseState(
                            ChatAudioPlaybackTransportStatus.Playing,
                            ReadPositionSeconds(player),
                            speed,
                            null);
                    }
                }).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private static uint ReadPositionSeconds(MediaPlayer player)
        {
            try
            {
                double secs = player.PlaybackSession.Position.TotalSeconds;
                if (secs < 0)
                {
                    secs = 0;
                }

                return (uint)Math.Floor(secs);
            }
            catch
            {
                return 0;
            }
        }

        private void RaiseState(
            ChatAudioPlaybackTransportStatus status,
            uint positionSeconds,
            AudioPlaybackSpeed speed,
            string error)
        {
            StateChanged?.Invoke(
                this,
                new ChatAudioPlaybackStateChangedEventArgs(status, positionSeconds, speed, error));
        }

        private async Task RunOnUiAsync(Action action)
        {
            CoreDispatcher dispatcher;
            lock (_gate)
            {
                dispatcher = _dispatcher;
            }

            if (dispatcher == null)
            {
                action();
                return;
            }

            await dispatcher.RunAsync(CoreDispatcherPriority.Normal, () => action());
        }
    }
}
