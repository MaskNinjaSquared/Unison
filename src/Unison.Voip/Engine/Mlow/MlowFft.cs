// =============================================================================
// MlowFft
//
// Single-precision recursive mixed-radix Cooley-Tukey DFT and the ordered real
// FFT layout the analysis front-end uses (f[0]=DC, f[1]=Nyquist, then re/im).
// Twiddles are evaluated with the reference's exact float32 angle expressions
// once per (length, direction) and cached; per-depth scratch is thread-local.
// Ports: meowcaller mlow/fft.go (MIT)
// =============================================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Unison.Voip.Engine.Mlow
{
    internal struct Cpx
    {
        public float Re;
        public float Im;

        public Cpx(float re, float im)
        {
            Re = re;
            Im = im;
        }

        public Cpx Add(Cpx b)
        {
            return new Cpx(Re + b.Re, Im + b.Im);
        }

        public Cpx Mul(Cpx b)
        {
            return new Cpx(Re * b.Re - Im * b.Im, Re * b.Im + Im * b.Re);
        }
    }

    internal static class MlowFft
    {
        /// <summary>One recursion depth: node length N, radix P (P == N for a prime leaf), and its twiddles.</summary>
        private sealed class Stage
        {
            public int N;
            public int P;
            public Cpx[] Tw;
        }

        private static readonly ConcurrentDictionary<long, Stage[]> Plans = new ConcurrentDictionary<long, Stage[]>();

        [ThreadStatic]
        private static Dictionary<long, Cpx[][]> _scratch;

        /// <summary>Smallest prime factor of n (n &gt;= 2).</summary>
        internal static int SmallestFactor(int n)
        {
            if (n % 2 == 0)
            {
                return 2;
            }

            int p = 3;
            while (p * p <= n)
            {
                if (n % p == 0)
                {
                    return p;
                }

                p += 2;
            }

            return n;
        }

        private static long PlanKey(int n, float sign)
        {
            return ((long)n << 1) | (sign > 0 ? 1L : 0L);
        }

        private static Stage[] BuildPlan(int n, float sign)
        {
            var stages = new List<Stage>();
            while (n > 1)
            {
                int p = SmallestFactor(n);
                var st = new Stage { N = n, P = p };
                if (p == n)
                {
                    st.Tw = new Cpx[n * n];
                    for (int k = 0; k < n; k++)
                    {
                        float angK = sign * 2.0f * MlowMath.SmplPI * (float)k / (float)n;
                        for (int j = 0; j < n; j++)
                        {
                            float ang = angK * (float)j;
                            st.Tw[k * n + j] = new Cpx((float)Math.Cos(ang), (float)Math.Sin(ang));
                        }
                    }

                    stages.Add(st);
                    break;
                }

                st.Tw = new Cpx[n * p];
                for (int k = 0; k < n; k++)
                {
                    for (int q = 0; q < p; q++)
                    {
                        float ang = sign * 2.0f * MlowMath.SmplPI * (float)k * (float)q / (float)n;
                        st.Tw[k * p + q] = new Cpx((float)Math.Cos(ang), (float)Math.Sin(ang));
                    }
                }

                stages.Add(st);
                n /= p;
            }

            return stages.ToArray();
        }

        private static Cpx[][] Scratch(long key, Stage[] plan)
        {
            if (_scratch == null)
            {
                _scratch = new Dictionary<long, Cpx[][]>();
            }

            Cpx[][] s;
            if (!_scratch.TryGetValue(key, out s))
            {
                s = new Cpx[plan.Length][];
                for (int d = 0; d < plan.Length; d++)
                {
                    s[d] = new Cpx[plan[d].N];
                }

                _scratch[key] = s;
            }

            return s;
        }

        /// <summary>Recursive DFT at plan depth d; x holds the node's inputs at stride from xOff.</summary>
        private static void FftRec(Cpx[] x, int xOff, int stride, Stage[] plan, int d, Cpx[][] scratch, Cpx[] o, int oOff)
        {
            if (d == plan.Length)
            {
                o[oOff] = x[xOff];
                return;
            }

            Stage st = plan[d];
            int n = st.N;
            int p = st.P;
            Cpx[] tw = st.Tw;
            if (p == n)
            {
                for (int k = 0; k < n; k++)
                {
                    float accRe = 0, accIm = 0;
                    int tb = k * n;
                    for (int j = 0; j < n; j++)
                    {
                        Cpx a = x[xOff + j * stride];
                        Cpx b = tw[tb + j];
                        accRe += a.Re * b.Re - a.Im * b.Im;
                        accIm += a.Re * b.Im + a.Im * b.Re;
                    }

                    o[oOff + k] = new Cpx(accRe, accIm);
                }

                return;
            }

            int m = n / p;
            Cpx[] sub = scratch[d];
            for (int q = 0; q < p; q++)
            {
                FftRec(x, xOff + q * stride, stride * p, plan, d + 1, scratch, sub, q * m);
            }

            for (int r = 0, k = 0; r < p; r++)
            {
                for (int kmod = 0; kmod < m; kmod++, k++)
                {
                    float accRe = 0, accIm = 0;
                    int tb = k * p;
                    for (int q = 0; q < p; q++)
                    {
                        Cpx a = sub[q * m + kmod];
                        Cpx b = tw[tb + q];
                        accRe += a.Re * b.Re - a.Im * b.Im;
                        accIm += a.Re * b.Im + a.Im * b.Re;
                    }

                    o[oOff + k] = new Cpx(accRe, accIm);
                }
            }
        }

        /// <summary>Complex FFT of a mixed-radix length; sign -1 forward, +1 inverse (unnormalized).</summary>
        internal static void Cfft(Cpx[] input, Cpx[] o, float sign)
        {
            int n = input.Length;
            long key = PlanKey(n, sign);
            Stage[] plan = Plans.GetOrAdd(key, k => BuildPlan(n, sign));
            FftRec(input, 0, 1, plan, 0, Scratch(key, plan), o, 0);
        }

        [ThreadStatic]
        private static Dictionary<int, Cpx[][]> _work;

        /// <summary>Thread-local pair of n-point complex work buffers (contents are stale).</summary>
        internal static Cpx[][] Work(int n)
        {
            if (_work == null)
            {
                _work = new Dictionary<int, Cpx[][]>();
            }

            Cpx[][] w;
            if (!_work.TryGetValue(n, out w))
            {
                w = new[] { new Cpx[n], new Cpx[n] };
                _work[n] = w;
            }

            return w;
        }

        /// <summary>Forward real FFT of n samples into the ordered REAL layout (length n).</summary>
        internal static void RfftForwardOrdered(float[] time, float[] f)
        {
            int n = time.Length;
            Cpx[][] w = Work(n);
            Cpx[] cin = w[0];
            for (int i = 0; i < n; i++)
            {
                cin[i] = new Cpx(time[i], 0);
            }

            Cpx[] spec = w[1];
            Cfft(cin, spec, -1.0f);
            f[0] = spec[0].Re;
            f[1] = spec[n / 2].Re;
            for (int i = 1; i < n / 2; i++)
            {
                f[2 * i] = spec[i].Re;
                f[2 * i + 1] = spec[i].Im;
            }
        }
    }
}
