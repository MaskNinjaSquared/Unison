using System;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Live handle for one unitary mic capture. Elapsed is readable while active;
    /// Stop/Cancel complete the session (service remains the owner of MediaCapture).
    /// </summary>
    public interface IAudioRecordingSession
    {
        /// <summary>False after Stop/Cancel or if superseded by a newer Start.</summary>
        bool IsActive { get; }

        DateTime StartedAtUtc { get; }

        /// <summary>UtcNow - StartedAtUtc while active; Zero when inactive.</summary>
        TimeSpan Elapsed { get; }

        /// <summary>Stops capture and returns the audio payload (voice note).</summary>
        Task<AudioRecordingResult> StopAsync();

        /// <summary>Stops and discards without returning bytes.</summary>
        Task CancelAsync();
    }
}
