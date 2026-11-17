using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

namespace Unison.Voip.Null
{
    /// <summary>Tests and hosts without media — every I/O op is a no-op or empty.</summary>
    public sealed class NullCallIoFactory : ICallIoFactory
    {
        public static NullCallIoFactory Instance { get; } = new NullCallIoFactory();

        public ICallAudioSource CreateAudioSource() => new NullAudioSource();

        public ICallAudioSink CreateAudioSink() => new NullAudioSink();

        public ICallUdpTransport CreateUdpTransport() => new NullUdpTransport();

        private sealed class NullAudioSource : ICallAudioSource
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public float[] TryReadFrame() => null;

            public Task<float[]> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromResult<float[]>(null);

            public void Dispose()
            {
            }
        }

        private sealed class NullAudioSink : ICallAudioSink
        {
            public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task WriteAsync(float[] pcm, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public void Dispose()
            {
            }
        }

        private sealed class NullUdpTransport : ICallUdpTransport
        {
            public Task BindAsync(CancellationToken cancellationToken) => Task.CompletedTask;

            public Task SendAsync(byte[] payload, IPEndPoint remote, CancellationToken cancellationToken) =>
                Task.CompletedTask;

            public Task<CallUdpDatagram> ReceiveAsync(CancellationToken cancellationToken) =>
                Task.FromResult<CallUdpDatagram>(null);

            public void Dispose()
            {
            }
        }
    }
}
