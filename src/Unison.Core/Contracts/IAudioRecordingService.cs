using System;
using System.Threading.Tasks;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Unitary microphone capture: at most one session in flight (singleton-friendly).
    /// Does not send WhatsApp messages — only captures bytes + duration.
    /// </summary>
    public interface IAudioRecordingService
    {
        /// <summary>True while <see cref="Current"/> is an active session.</summary>
        bool IsRecording { get; }

        /// <summary>Active session handle, or null when idle.</summary>
        IAudioRecordingSession Current { get; }

        /// <summary>
        /// Starts a new recording (cancels any previous session first).
        /// Returns a live handle with <see cref="IAudioRecordingSession.Elapsed"/>.
        /// </summary>
        Task<IAudioRecordingSession> StartAsync();
    }
}
