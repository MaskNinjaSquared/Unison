using System;
using System.Diagnostics;
using Unison.Core.Contracts;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    /// <summary>Plays packaged sound cues; failures are swallowed so UI never depends on audio.</summary>
    public sealed class UiSoundService : IUiSoundService
    {
        private MediaPlayer _player;

        /// <summary>Unigram <c>Assets/Audio/sent.mp3</c> — short cue after a successful send.</summary>
        public void PlayMessageSent() => Play("ms-appx:///Assets/Sounds/message_sent.mp3");

        public void PlayVoiceNoteStart() => Play("ms-appx:///Assets/Sounds/voice_note_start.wav");

        public void PlayVoiceNoteStop() => Play("ms-appx:///Assets/Sounds/voice_note_stop.wav");

        public void PlayVoiceNoteError() => Play("ms-appx:///Assets/Sounds/voice_note_error.wav");

        private void Play(string uri)
        {
            try
            {
                if (_player == null)
                {
                    _player = new MediaPlayer
                    {
                        AudioCategory = MediaPlayerAudioCategory.SoundEffects,
                        IsLoopingEnabled = false,
                        Volume = 1.0
                    };
                }

                _player.Source = MediaSource.CreateFromUri(new Uri(uri));
                _player.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[UiSoundService] Play failed: " + ex.Message);
            }
        }
    }
}
