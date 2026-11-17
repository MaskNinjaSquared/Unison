// =============================================================================
// MlowDecoder
//
// Stateful MLow decoder: RED strip -> TOC routing -> active-frame decode (three
// chained 20 ms internal frames: LSF -> pulses -> pitch/gains -> reconstruct ->
// CELP synthesis) -> per-packet harmonic postfilter -> 60 ms PCM. Predictor and
// synthesis history persist across calls (the stream is continuous).
// Ports: meowcaller mlow/decoder.go (MIT)
// =============================================================================
using System.Collections.Generic;
using Unison.Voip.Engine.Media;

namespace Unison.Voip.Engine.Mlow
{
    /// <summary>Cross-frame decoder state: LSF predictor, previous NLSF, CELP and harmonic-postfilter state.</summary>
    internal sealed class SmplDecoderState
    {
        public readonly SmplLsfState Lstate = new SmplLsfState();
        public float[] PrevNlsf;
        public readonly CelpDecState Celp = new CelpDecState();
        public readonly HarmPostfilterState Harm = new HarmPostfilterState();
    }

    public sealed class MlowDecoder : IAudioFrameDecoder
    {
        /// <summary>60 ms at 16 kHz.</summary>
        public const int FrameSamples = 960;

        private const int InternalFrames = 3;

        private SmplDecoderState _state = new SmplDecoderState();
        private int _redundancy;
        private uint _droppedUnsupported;

        internal SmplDecoderState State => _state;

        /// <summary>Active frames dropped for an unsupported operating point (not 16 kHz / 60 ms).</summary>
        internal uint DroppedUnsupported => _droppedUnsupported;

        /// <summary>Negotiated RED redundancy level (0 = bare frames).</summary>
        public void SetRedundancy(int n)
        {
            _redundancy = n;
        }

        /// <summary>Clears the cross-frame state (call at a stream discontinuity).</summary>
        public void Reset()
        {
            _state = new SmplDecoderState();
        }

        /// <summary>Decodes one RTP MLow payload into 60 ms (960-sample) frames of float PCM in [-1, 1].</summary>
        public float[] Decode(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
            {
                return new float[FrameSamples];
            }

            List<byte[]> subs;
            if (TrySplitContainer(payload, out subs))
            {
                var o = new List<float>(subs.Count * FrameSamples);
                foreach (byte[] sf in subs)
                {
                    o.AddRange(DecodeFrame(sf));
                }

                return o.ToArray();
            }

            if (_redundancy > 0)
            {
                MlowFrame[] frames;
                SplitRedError err;
                if (!SplitRed.TryDepack(payload, out frames, out err))
                {
                    return new float[FrameSamples];
                }

                byte[] main = frames.Length > 0 ? frames[frames.Length - 1].Data : null;
                return DecodeFrame(main);
            }

            return DecodeFrame(payload);
        }

        /// <summary>
        /// 0x92 multi-frame container: 0x92 &lt;count&gt; [&lt;len&gt; &lt;frame&gt;]*(count-1) &lt;last frame = rest&gt;.
        /// False for anything not well formed, so the payload decodes as a bare frame.
        /// </summary>
        internal static bool TrySplitContainer(byte[] p, out List<byte[]> frames)
        {
            frames = null;
            if (p.Length < 3 || p[0] != 0x92)
            {
                return false;
            }

            int count = p[1];
            if (count < 2 || count > 8)
            {
                return false;
            }

            var o = new List<byte[]>(count);
            int off = 2;
            for (int i = 0; i < count - 1; i++)
            {
                if (off >= p.Length)
                {
                    return false;
                }

                int flen = p[off];
                off++;
                if (off + flen > p.Length)
                {
                    return false;
                }

                o.Add(Slice(p, off, flen));
                off += flen;
            }

            if (off >= p.Length)
            {
                return false;
            }

            o.Add(Slice(p, off, p.Length - off));
            frames = o;
            return true;
        }

        private static byte[] Slice(byte[] p, int offset, int count)
        {
            var o = new byte[count];
            System.Buffer.BlockCopy(p, offset, o, 0, count);
            return o;
        }

        private float[] DecodeFrame(byte[] frame)
        {
            if (frame == null || frame.Length == 0)
            {
                return new float[FrameSamples];
            }

            SmplToc toc = SmplToc.Parse(frame[0]);
            int outLen = toc.StdOpus ? 16000 / 1000 * toc.FrameMs : toc.SampleRate / 1000 * toc.FrameMs;
            if (toc.StdOpus)
            {
                return new float[outLen];
            }

            if (toc.Sid || !toc.Active)
            {
                return new float[outLen];
            }

            if (toc.SampleRate != 16000 || toc.FrameMs != 60)
            {
                _droppedUnsupported++;
                return new float[FrameSamples];
            }

            return DecodeActiveFrame(frame, outLen);
        }

        private float[] DecodeActiveFrame(byte[] frame, int outLen)
        {
            int config = (frame[0] >> 2) & 1;
            SmplTables tbl = SmplLsf.LoadSmplTables();
            SmplSynthTables synthT = SmplSynth.LoadSmplSynthTables();
            SmplMem mem = SmplMem.Load();
            var dec = new RangeDecoder(frame, 1, frame.Length - 1);
            bool lowRate = ((frame[0] >> 2) & 1) != 0;
            int numSubframes = lowRate ? 2 : 4;
            const int intfLen = SmplSynth.SmplIntfLen;

            var o = new float[InternalFrames * intfLen];
            var packetLags = new float[InternalFrames * 8];
            float avgNormBr = 0;
            for (int f = 0; f < InternalFrames; f++)
            {
                SmplLsfIndices lsf = SmplLsf.DecodeSmplLsf(dec, tbl, _state.Lstate, config, f);
                SmplPulseResult pulses = SmplPulse.DecodeSmplPulses(dec, mem, intfLen, numSubframes, 1, config, lsf.Stage1);
                bool voiced = lsf.Stage1 == 1;
                int total = 0;
                foreach (int c in pulses.Subfr)
                {
                    total += c;
                }

                var p = new CelpDecParams { Voiced = voiced, TotalPulses = total };
                System.Array.Copy(pulses.Subfr, p.SfPulses, 4);
                if (voiced)
                {
                    SmplPitchResult pr = SmplPitch.DecodeSmplPitch(dec, mem, _state.Lstate, intfLen, numSubframes, config, pulses.Subfr);
                    for (int b = 0; b < 8; b++)
                    {
                        double v = (double)pr.BlockLags[b] * 0.5 + 32.0;
                        if (v > 320.0)
                        {
                            v = 320.0;
                        }

                        p.BlockLags[b] = (float)v;
                    }

                    for (int sf = 0; sf < numSubframes; sf++)
                    {
                        p.AcbgIdx[sf] = pr.GainIdx[sf];
                        if (pr.FiltIdx[sf] > 0)
                        {
                            p.FcbgIdx[sf] = pr.FiltIdx[sf];
                        }
                    }
                }
                else
                {
                    SmplGainResult g = SmplGains.DecodeSmplGains(dec, mem, numSubframes, pulses.Subfr);
                    System.Array.Copy(g.GainQ, p.NrgresDbqQ14, 4);
                    System.Array.Copy(g.NrgRes, p.FcbgIdx, 4);
                }

                System.Array.Copy(p.BlockLags, 0, packetLags, f * 8, 8);
                avgNormBr += SmplNoise.SmplGetNormalizedBitrate(p.TotalPulses, intfLen);

                float[] nlsf = SmplSynth.SmplReconstructNLSF(synthT, lsf.Stage1, config, lsf.Grid, lsf.Stage2, _state.PrevNlsf);
                var sig = new float[intfLen];
                _state.Celp.SynthFrame(nlsf, lsf.Extra, pulses.Pulses, p, lowRate, intfLen, sig);
                _state.PrevNlsf = nlsf;
                System.Array.Copy(sig, 0, o, f * intfLen, intfLen);
            }

            SmplPostfilter.SmplHarmPostfilter(_state.Harm, o, o.Length, packetLags, packetLags.Length, avgNormBr / 3.0f);

            var pcm = new float[o.Length];
            for (int i = 0; i < o.Length; i++)
            {
                float v = o[i];
                if (v > 1.0f)
                {
                    v = 1.0f;
                }
                else if (v < -1.0f)
                {
                    v = -1.0f;
                }

                pcm[i] = v;
            }

            if (outLen > 0 && outLen != pcm.Length)
            {
                var np = new float[outLen];
                System.Array.Copy(pcm, np, System.Math.Min(outLen, pcm.Length));
                pcm = np;
            }

            return pcm;
        }
    }
}
