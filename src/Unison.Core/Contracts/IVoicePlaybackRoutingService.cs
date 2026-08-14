using System;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Routes voice-note playback between loudspeaker (default) and earpiece
    /// (proximity / held to ear), including Mobile screen-off while near the ear.
    /// Platform-specific; Core only sees the abstract session contract.
    /// </summary>
    public interface IVoicePlaybackRoutingService
    {
        /// <summary>True on handsets that can switch speaker ↔ earpiece.</summary>
        bool IsSupported { get; }

        /// <summary>Current intended route for the active session (Speaker when idle).</summary>
        VoicePlaybackRoute CurrentRoute { get; }

        /// <summary>
        /// Suggested <c>MediaPlayer.AudioCategory</c> name for voice notes on this device
        /// (<c>Communications</c> on Mobile so routing APIs work; <c>Media</c> elsewhere).
        /// </summary>
        string PreferredAudioCategory { get; }

        /// <summary>Raised when proximity or policy changes the active route.</summary>
        event EventHandler<VoicePlaybackRoute> RouteChanged;

        /// <summary>
        /// Bind the platform media player (UWP <c>MediaPlayer</c>) so category /
        /// routing can be applied. Safe to call repeatedly with the same instance.
        /// </summary>
        void AttachPlayer(object mediaPlayer);

        /// <summary>Drop the player reference (does not dispose the player).</summary>
        void DetachPlayer();

        /// <summary>
        /// Start a voice playback session: default loudspeaker + proximity watch.
        /// Call when playback is starting / has started (after <c>Play()</c>).
        /// </summary>
        void BeginSession();

        /// <summary>
        /// End the session: detach proximity, release screen controller, restore speaker.
        /// </summary>
        void EndSession();
    }

    public enum VoicePlaybackRoute
    {
        Speaker = 0,
        Earpiece = 1
    }
}
