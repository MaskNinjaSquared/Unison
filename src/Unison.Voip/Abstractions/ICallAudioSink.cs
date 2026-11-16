using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Plays PCM from the peer (meowcaller <c>Sink</c>). UWP owns the audio graph.
    /// </summary>
    public interface ICallAudioSink : IDisposable
    {
        Task WriteAsync(float[] pcm, CancellationToken cancellationToken);
    }
}
