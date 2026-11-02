namespace Unison.Core.Models
{
    /// <summary>Composer voice-note capture state (mic bar), not bubble playback.</summary>
    public enum VoiceRecordingState
    {
        /// <summary>No capture in progress.</summary>
        Pristine = 0,

        /// <summary>Mic is capturing (elapsed advances).</summary>
        Playing = 1,

        /// <summary>Capture paused (elapsed frozen).</summary>
        Paused = 2
    }
}
