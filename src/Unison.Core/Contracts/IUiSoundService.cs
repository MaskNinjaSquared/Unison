namespace Unison.Core.Contracts
{
    /// <summary>Short UI sound effects (voice-note chrome, sent cue, etc.).</summary>
    public interface IUiSoundService
    {
        void PlayMessageSent();

        void PlayVoiceNoteStart();

        void PlayVoiceNoteStop();

        void PlayVoiceNoteError();
    }
}
