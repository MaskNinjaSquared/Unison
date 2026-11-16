using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Unison.Voip.Abstractions;

namespace Unison.Uwp.Services.Calls
{
    /// <summary>
    /// Stub I/O factory — DatagramSocket / MediaCapture land in Fase 4 after Opus review.
    /// </summary>
    public sealed class UwpCallIoFactory : ICallIoFactory
    {
        public ICallAudioSource CreateAudioSource() => new StubAudioSource();

        public ICallAudioSink CreateAudioSink() => new StubAudioSink();

        public ICallUdpTransport CreateUdpTransport() => new StubUdpTransport();

        private sealed class StubAudioSource : ICallAudioSource
        {
            public Task<float[]> ReadAsync(CancellationToken cancellationToken) =>
                Task.FromException<float[]>(
                    new NotSupportedException("UWP call audio capture is not wired yet."));

            public void Dispose()
            {
            }
        }

        private sealed class StubAudioSink : ICallAudioSink
        {
            public Task WriteAsync(float[] pcm, CancellationToken cancellationToken) =>
                Task.FromException(
                    new NotSupportedException("UWP call audio playback is not wired yet."));

            public void Dispose()
            {
            }
        }

        private sealed class StubUdpTransport : ICallUdpTransport
        {
            public Task BindAsync(CancellationToken cancellationToken) =>
                Task.FromException(
                    new NotSupportedException("UWP DatagramSocket for calls is not wired yet."));

            public Task SendAsync(byte[] payload, IPEndPoint remote, CancellationToken cancellationToken) =>
                Task.FromException(
                    new NotSupportedException("UWP DatagramSocket for calls is not wired yet."));

            public Task<CallUdpDatagram> ReceiveAsync(CancellationToken cancellationToken) =>
                Task.FromException<CallUdpDatagram>(
                    new NotSupportedException("UWP DatagramSocket for calls is not wired yet."));

            public void Dispose()
            {
            }
        }
    }
}
