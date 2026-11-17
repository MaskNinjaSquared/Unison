using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Captures PCM for an active call (meowcaller <c>Source</c>). Host owns the device
    /// (UWP: AudioGraph). Frames are mono float32, 960 samples at 16 kHz.
    /// </summary>
    public interface ICallAudioSource : IDisposable
    {
        /// <summary>Opens the capture device. Idempotent.</summary>
        Task StartAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Non-blocking: the next complete frame, or null when none is ready (caller sends silence).
        /// </summary>
        float[] TryReadFrame();

        /// <summary>Waits for the next frame, or null when the source ended.</summary>
        Task<float[]> ReadAsync(CancellationToken cancellationToken);
    }
}
