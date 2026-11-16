namespace Unison.Voip.Abstractions
{
    /// <summary>
    /// Host constructs audio + UDP for one call. UWP implementation; Voip stays portable.
    /// </summary>
    public interface ICallIoFactory
    {
        ICallAudioSource CreateAudioSource();

        ICallAudioSink CreateAudioSink();

        ICallUdpTransport CreateUdpTransport();
    }
}
