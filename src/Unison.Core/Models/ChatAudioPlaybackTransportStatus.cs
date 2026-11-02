namespace Unison.Core.Models
{
    /// <summary>Logical transport state from <see cref="Contracts.IChatAudioPlaybackService"/>.</summary>
    public enum ChatAudioPlaybackTransportStatus
    {
        Stopped = 0,
        Playing = 1,
        Paused = 2,
        Failed = 3
    }
}
