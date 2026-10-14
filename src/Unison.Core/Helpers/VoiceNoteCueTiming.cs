namespace Unison.Core.Helpers
{
    /// <summary>
    /// How long the voice-note cues take. Microphone access is checked before the start cue;
    /// capture begins only after that cue. The stop cue plays after the recording is finalized.
    /// Change the numbers here to retune both.
    /// </summary>
    public static class VoiceNoteCueTiming
    {
        /// <summary>Start cue length. Capture begins after this.</summary>
        public const int StartRecordingDelayMs = 2000;

        /// <summary>How long after finalize before the stop cue plays.</summary>
        public const int StopSoundDelayMs = 2000;
    }
}
