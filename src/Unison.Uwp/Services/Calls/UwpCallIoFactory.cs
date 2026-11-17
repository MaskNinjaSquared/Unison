// =============================================================================
// UwpCallIoFactory
//
// Real DatagramSocket UDP + AudioGraph mic/speaker for calls.
// =============================================================================
using Unison.Voip.Abstractions;

namespace Unison.Uwp.Services.Calls
{
    /// <summary>UWP I/O for calls: DatagramSocket UDP and AudioGraph Source/Sink.</summary>
    public sealed class UwpCallIoFactory : ICallIoFactory
    {
        public ICallAudioSource CreateAudioSource() => new UwpCallAudioSource();

        public ICallAudioSink CreateAudioSink() => new UwpCallAudioSink();

        public ICallUdpTransport CreateUdpTransport() => new UwpDatagramCallUdpTransport();
    }
}
