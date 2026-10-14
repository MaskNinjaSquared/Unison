namespace Unison.Core.Contracts
{
    /// <summary>Short UI sound effects (voice-note chrome, etc.).</summary>
    public interface IUiSoundService
    {
        void PlayVoiceNoteStart();

        void PlayVoiceNoteStop();

        void PlayVoiceNoteError();
    }
}
