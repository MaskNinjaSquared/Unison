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

        /// <summary>True while capture is paused (elapsed frozen).</summary>
        bool IsPaused { get; }

        DateTime StartedAtUtc { get; }

        /// <summary>Active capture time excluding pauses; Zero when inactive.</summary>
        TimeSpan Elapsed { get; }

        /// <summary>Pauses capture without finishing the session.</summary>
        Task PauseAsync();

        /// <summary>Resumes a paused capture.</summary>
        Task ResumeAsync();

        /// <summary>Stops capture and returns the audio payload (voice note).</summary>
        Task<AudioRecordingResult> StopAsync();

        /// <summary>Stops and discards without returning bytes.</summary>
        Task CancelAsync();
    }
}
