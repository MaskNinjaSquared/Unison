namespace Unison.Core.Contracts
{
    /// <summary>
    /// Participant in exclusive in-bubble GIF playback (Unsupported / 512 MB).
    /// </summary>
    public interface IGifPlaybackClient
    {
        void PauseFromCoordinator();
    }

    /// <summary>
    /// Ensures at most one GIF plays at a time on <see cref="Models.DeviceCategory.Unsupported"/>.
    /// Low+ subscribers register too, but <see cref="NotifyPlaying"/> is a no-op there.
    /// </summary>
    public interface IGifPlaybackCoordinator
    {
        void Register(IGifPlaybackClient client);

        void Unregister(IGifPlaybackClient client);

        /// <summary>
        /// Call when a client starts playing. On Unsupported, pauses every other registered client.
        /// </summary>
        void NotifyPlaying(IGifPlaybackClient client);
    }
}
