using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Plays PCM from the peer (meowcaller <c>Sink</c>). Host owns the device
    /// (UWP: AudioGraph). Frames are mono float32, 960 samples at 16 kHz.
    /// </summary>
    public interface ICallAudioSink : IDisposable
    {
        /// <summary>Opens the render device. Idempotent.</summary>
        Task StartAsync(CancellationToken cancellationToken);

        Task WriteAsync(float[] pcm, CancellationToken cancellationToken);
    }
}
