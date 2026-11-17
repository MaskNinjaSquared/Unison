// =============================================================================
// MlowEncoder
//
// Stateful MLow encoder: sanitize (NaN -> 0, clamp [-1, 1]) -> analysis
// (PCM -> frame parameters) -> entropy code. The cross-frame analysis history
// persists across Encode calls (the stream is continuous).
// Ports: meowcaller mlow/encoder.go (MIT)
// =============================================================================
using System;
using Unison.Voip.Engine.Media;

namespace Unison.Voip.Engine.Mlow
{
    public sealed class MlowEncoder : IAudioFrameEncoder
    {
        /// <summary>60 ms at 16 kHz.</summary>
        public const int FrameSamples = 960;

        private SmplEncoderState _state = new SmplEncoderState();
        private readonly float[] _clean = new float[FrameSamples];

        /// <summary>Clears the cross-frame analysis history (call at a stream discontinuity).</summary>
        public void Reset()
        {
            _state = new SmplEncoderState();
        }

        /// <summary>
        /// Encodes one 60 ms frame (exactly 960 samples, nominally [-1, 1]) into a wire MLow frame.
        /// Returns null when the frame cannot be coded (range-encoder overflow).
        /// </summary>
        public byte[] Encode(float[] pcm)
        {
            if (pcm == null)
            {
                throw new ArgumentNullException(nameof(pcm));
            }

            if (pcm.Length != FrameSamples)
            {
                throw new ArgumentException("MLow encode expects " + FrameSamples + " samples (60 ms at 16 kHz), got " + pcm.Length + ".", nameof(pcm));
            }

            float[] clean = _clean;
            for (int i = 0; i < FrameSamples; i++)
            {
                float s = pcm[i];
                if (float.IsNaN(s))
                {
                    s = 0.0f;
                }
                else if (s < -1.0f)
                {
                    s = -1.0f;
                }
                else if (s > 1.0f)
                {
                    s = 1.0f;
                }

                clean[i] = s;
            }

            SmplFrameParams fp = SmplAnalysis.AnalyzeFrame(_state, clean);
            return SmplEncode.EncodeSmplFrame(fp);
        }
    }
}
