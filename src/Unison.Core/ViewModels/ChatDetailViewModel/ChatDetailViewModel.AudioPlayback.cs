using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Models;

namespace Unison.Core.ViewModels
{
    public partial class ChatDetailViewModel
    {
        private readonly IChatAudioPlaybackService _audioPlayback;
        private ChatMessageViewModel _playingAudioVm;
        private bool _audioStateHandlerAttached;

        /// <summary>Bind the shell MediaPlayerElement (opaque object from UWP).</summary>
        public void AttachAudioPlaybackUi(object mediaPlayerElement)
        {
            if (_audioPlayback == null)
            {
                return;
            }

            EnsureAudioStateHandler();
            _audioPlayback.AttachUi(mediaPlayerElement);
        }

        public void DetachAudioPlaybackUi()
        {
            if (_audioPlayback == null)
            {
                return;
            }

            _audioPlayback.Stop();
            _audioPlayback.DetachUi();
            ClearPlayingAudioBubble();
        }

        /// <summary>Stop the current voice note without detaching the MediaPlayerElement.</summary>
        public void StopAudioPlayback()
        {
            _audioPlayback?.Stop();
            if (_playingAudioVm != null)
            {
                _playingAudioVm.ResetAudioPlaybackToReady();
                ClearPlayingAudioBubble();
            }
        }

        public async Task PlayOrPauseAudioAsync(ChatMessageViewModel vm)
        {
            if (_audioPlayback == null || vm == null)
            {
                return;
            }

            if (await vm.NotifyIfMediaDeletedAsync().ConfigureAwait(true))
            {
                return;
            }

            ChatMessage message = vm.Model;
            if (message == null || !message.IsAudio)
            {
                return;
            }

            EnsureAudioStateHandler();

            try
            {
                if (ReferenceEquals(_playingAudioVm, vm) &&
                    vm.AudioPlaybackStatus == AudioPlaybackStatus.Playing)
                {
                    _audioPlayback.Pause();
                    return;
                }

                if (ReferenceEquals(_playingAudioVm, vm) &&
                    vm.AudioPlaybackStatus == AudioPlaybackStatus.Paused)
                {
                    ApplySmtcMetadata(message);
                    _audioPlayback.SetSpeed(vm.AudioPlaybackSpeed);
                    _audioPlayback.Resume();
                    return;
                }

                if (string.IsNullOrWhiteSpace(message.AudioUri))
                {
                    vm.AudioPlaybackStatus = AudioPlaybackStatus.Downloading;
                }

                string uri = await vm.EnsureAudioReadyAsync(showErrorDialog: true).ConfigureAwait(true);
                if (string.IsNullOrWhiteSpace(uri))
                {
                    vm.MarkAudioUnavailable();
                    return;
                }

                if (_playingAudioVm != null && !ReferenceEquals(_playingAudioVm, vm))
                {
                    _playingAudioVm.ResetAudioPlaybackToReady();
                }

                _playingAudioVm = vm;
                vm.AudioPlaybackPositionSeconds = 0;
                vm.AudioPlaybackStatus = AudioPlaybackStatus.Playing;
                ApplySmtcMetadata(message);
                await _audioPlayback.PlayAsync(new Uri(uri), vm.AudioPlaybackSpeed).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChatDetailViewModel] PlayOrPauseAudio failed: " + ex.Message);
                vm.MarkAudioUnavailable();
            }
        }

        public async Task PlayAudioAtSpeedAsync(ChatMessageViewModel vm, AudioPlaybackSpeed speed)
        {
            if (vm == null)
            {
                return;
            }

            vm.AudioPlaybackSpeed = speed;
            if (ReferenceEquals(_playingAudioVm, vm) &&
                (vm.AudioPlaybackStatus == AudioPlaybackStatus.Playing ||
                 vm.AudioPlaybackStatus == AudioPlaybackStatus.Paused))
            {
                _audioPlayback?.SetSpeed(speed);
                if (vm.AudioPlaybackStatus == AudioPlaybackStatus.Paused)
                {
                    _audioPlayback?.Resume();
                }

                return;
            }

            await PlayOrPauseAudioAsync(vm).ConfigureAwait(true);
        }

        public void CycleAudioPlaybackSpeed(ChatMessageViewModel vm)
        {
            if (vm == null)
            {
                return;
            }

            vm.CycleAudioPlaybackSpeed();
            if (ReferenceEquals(_playingAudioVm, vm) && _audioPlayback != null)
            {
                _audioPlayback.SetSpeed(vm.AudioPlaybackSpeed);
            }
        }

        public void SeekAudioPlayback(ChatMessageViewModel vm, double seconds)
        {
            if (vm == null ||
                _audioPlayback == null ||
                !ReferenceEquals(_playingAudioVm, vm))
            {
                return;
            }

            if (seconds < 0)
            {
                seconds = 0;
            }

            _audioPlayback.Seek(TimeSpan.FromSeconds(seconds));
            vm.AudioPlaybackPositionSeconds = (uint)Math.Floor(seconds);
        }

        private void EnsureAudioStateHandler()
        {
            if (_audioPlayback == null || _audioStateHandlerAttached)
            {
                return;
            }

            _audioPlayback.StateChanged += OnAudioPlaybackStateChanged;
            _audioStateHandlerAttached = true;
        }

        private void OnAudioPlaybackStateChanged(object sender, ChatAudioPlaybackStateChangedEventArgs e)
        {
            if (e == null)
            {
                return;
            }

            void Apply()
            {
                ChatMessageViewModel vm = _playingAudioVm;
                if (vm == null)
                {
                    return;
                }

                vm.AudioPlaybackPositionSeconds = e.PositionSeconds;
                vm.AudioPlaybackSpeed = e.Speed;

                switch (e.Status)
                {
                    case ChatAudioPlaybackTransportStatus.Playing:
                        vm.AudioPlaybackStatus = AudioPlaybackStatus.Playing;
                        break;
                    case ChatAudioPlaybackTransportStatus.Paused:
                        vm.AudioPlaybackStatus = AudioPlaybackStatus.Paused;
                        break;
                    case ChatAudioPlaybackTransportStatus.Stopped:
                        vm.ResetAudioPlaybackToReady();
                        ClearPlayingAudioBubble();
                        break;
                    case ChatAudioPlaybackTransportStatus.Failed:
                        vm.MarkAudioUnavailable();
                        ClearPlayingAudioBubble();
                        break;
                }
            }

            if (_dispatcher != null)
            {
                _ = _dispatcher.RunAsync(Apply);
            }
            else
            {
                Apply();
            }
        }

        private void ClearPlayingAudioBubble()
        {
            _playingAudioVm = null;
        }

        private void ApplySmtcMetadata(ChatMessage message)
        {
            if (_audioPlayback == null || message == null)
            {
                return;
            }

            string title;
            string artist;
            ResolveAudioSmtcMetadata(message, out title, out artist);
            _audioPlayback.SetDisplayMetadata(title, artist);
        }

        private void ResolveAudioSmtcMetadata(ChatMessage message, out string title, out string artist)
        {
            ChatItem chat = ActiveChat;
            bool isGroup = chat != null &&
                (chat.IsGroup ||
                 (!string.IsNullOrWhiteSpace(chat.JID) &&
                  chat.JID.IndexOf("@g.us", StringComparison.OrdinalIgnoreCase) >= 0));

            artist = !string.IsNullOrWhiteSpace(message.SenderName)
                ? message.SenderName.Trim()
                : (chat?.Name ?? "WhatsApp");

            if (message.IsFromMe)
            {
                string self = _strings != null
                    ? _strings.Get("Chat_SelfFallbackName", "You")
                    : "You";
                artist = self;
            }

            if (isGroup)
            {
                title = !string.IsNullOrWhiteSpace(chat?.Name) ? chat.Name.Trim() : "Group";
            }
            else
            {
                title = "Unison";
            }
        }

        private void OnBubbleAudioPlayPauseRequested(object sender, EventArgs e)
        {
            _ = PlayOrPauseAudioAsync(sender as ChatMessageViewModel);
        }

        private void OnBubbleAudioCycleSpeedRequested(object sender, EventArgs e)
        {
            CycleAudioPlaybackSpeed(sender as ChatMessageViewModel);
        }

        private void OnBubbleAudioPlayAtSpeedRequested(object sender, AudioPlaybackSpeed speed)
        {
            _ = PlayAudioAtSpeedAsync(sender as ChatMessageViewModel, speed);
        }
    }
}
