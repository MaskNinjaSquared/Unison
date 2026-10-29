// =============================================================================
// IChatAudioPlaybackService
//
// Platform-owned voice/audio player. Core sees play/pause/seek/speed and state
// events only — no MediaPlayer / WinRT types.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Single-slot chat audio player. UWP owns the MediaPlayer; ViewModels orchestrate.
    /// </summary>
    public interface IChatAudioPlaybackService
    {
        /// <summary>Raised on play/pause/stop/fail and ~4×/s while playing (position).</summary>
        event EventHandler<ChatAudioPlaybackStateChangedEventArgs> StateChanged;

        /// <summary>
        /// Bind the shell <c>MediaPlayerElement</c> (UWP passes the element; Core stays opaque).
        /// </summary>
        void AttachUi(object mediaPlayerElement);

        void DetachUi();

        /// <summary>Optional SMTC title/artist; re-applied after Source changes.</summary>
        void SetDisplayMetadata(string title, string artist);

        Task PlayAsync(Uri source, AudioPlaybackSpeed speed);

        void Pause();

        void Resume();

        void Stop();

        void Seek(TimeSpan position);

        void SetSpeed(AudioPlaybackSpeed speed);

        AudioPlaybackSpeed Speed { get; }

        bool IsPlaying { get; }
    }
}
