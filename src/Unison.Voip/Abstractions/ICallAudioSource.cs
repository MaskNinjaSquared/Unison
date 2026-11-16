using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Captures PCM for an active call (meowcaller <c>Source</c>). UWP owns MediaCapture.
    /// </summary>
    public interface ICallAudioSource : IDisposable
    {
        /// <summary>Pulls the next mono float32 frame, or null when the source ended.</summary>
        Task<float[]> ReadAsync(CancellationToken cancellationToken);
    }
}
