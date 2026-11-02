using System;

namespace Unison.Core.Models
{
    /// <summary>Snapshot pushed by <see cref="Contracts.IChatAudioPlaybackService.StateChanged"/>.</summary>
    public sealed class ChatAudioPlaybackStateChangedEventArgs : EventArgs
    {
        public ChatAudioPlaybackStateChangedEventArgs(
            ChatAudioPlaybackTransportStatus status,
            uint positionSeconds,
            AudioPlaybackSpeed speed,
            string errorMessage = null)
        {
            Status = status;
            PositionSeconds = positionSeconds;
            Speed = speed;
            ErrorMessage = errorMessage;
        }

        public ChatAudioPlaybackTransportStatus Status { get; }

        public uint PositionSeconds { get; }

        public AudioPlaybackSpeed Speed { get; }

        public string ErrorMessage { get; }
    }
}
