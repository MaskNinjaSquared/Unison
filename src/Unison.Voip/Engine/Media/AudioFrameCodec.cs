// =============================================================================
// AudioFrameCodec
//
// Frame codec seam for the relay media loop (MLow lands behind these). Frames
// are mono float32 PCM at SampleRate, FrameSamples long.
// Ports: meowcaller audio.go constants (MIT)
// =============================================================================
namespace Unison.Voip.Engine.Media
{
    public static class AudioFrameFormat
    {
        public const int SampleRate = 16000;

        /// <summary>60 ms at 16 kHz.</summary>
        public const int FrameSamples = 960;
    }

    public interface IAudioFrameEncoder
    {
        /// <summary>Encodes one frame; returns null when the frame could not be coded.</summary>
        byte[] Encode(float[] pcm);
    }

    public interface IAudioFrameDecoder
    {
        /// <summary>Decodes one payload; returns null on a malformed payload.</summary>
        float[] Decode(byte[] payload);
    }
}
