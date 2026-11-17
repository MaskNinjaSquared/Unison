// =============================================================================
// MlowMath
//
// Single-precision scalar helpers and shared constants for the MLow codec. All
// float32 math rounds per operation like the Go/C reference; transcendental
// calls widen to double and narrow back exactly where the reference does.
// Ports: meowcaller mlow/lsf_quant.go, noise.go, lpc.go (helpers, MIT)
// =============================================================================
using System;
using System.Runtime.InteropServices;

namespace Unison.Voip.Engine.Mlow
{
    internal static class MlowMath
    {
        public const int SmplLpcOrder = 16;

        /// <summary>The reference's truncated pi literal (not Math.PI), narrowed to float32.</summary>
        public const float SmplPI = 3.1415926535897f;

        /// <summary>float32(math.Pi).</summary>
        public const float SmplPi = (float)Math.PI;

        private const double InvLn2 = 1.4426950408889634;

        [StructLayout(LayoutKind.Explicit)]
        private struct F32Bits
        {
            [FieldOffset(0)]
            public float F;

            [FieldOffset(0)]
            public uint U;
        }

        public static float CosF32(float x)
        {
            return (float)Math.Cos(x);
        }

        public static float SinF32(float x)
        {
            return (float)Math.Sin(x);
        }

        public static float SqrtF32(float x)
        {
            return (float)Math.Sqrt(x);
        }

        public static float Log2F32(float x)
        {
            return (float)Log2(x);
        }

        public static float RoundF32(float x)
        {
            return (float)Math.Round((double)x, MidpointRounding.AwayFromZero);
        }

        public static float AbsF32(float x)
        {
            return x < 0 ? -x : x;
        }

        public static float MaxF32(float a, float b)
        {
            return a > b ? a : b;
        }

        public static float MinF32(float a, float b)
        {
            return a < b ? a : b;
        }

        /// <summary>Go math.Round: half away from zero.</summary>
        public static double GoRound(double x)
        {
            return Math.Round(x, MidpointRounding.AwayFromZero);
        }

        /// <summary>Go math.Log2: frexp split so exact powers of two are exact.</summary>
        public static double Log2(double x)
        {
            int exp;
            double frac = Frexp(x, out exp);
            if (frac == 0.5)
            {
                return exp - 1;
            }

            return Math.Log(frac) * InvLn2 + exp;
        }

        /// <summary>Go math.Frexp: x = frac * 2^exp with |frac| in [0.5, 1).</summary>
        public static double Frexp(double x, out int exp)
        {
            exp = 0;
            if (x == 0 || double.IsInfinity(x) || double.IsNaN(x))
            {
                return x;
            }

            if (Math.Abs(x) < 2.2250738585072014e-308)
            {
                x *= 4503599627370496.0;
                exp = -52;
            }

            long bits = BitConverter.DoubleToInt64Bits(x);
            exp += (int)((bits >> 52) & 0x7ff) - 1022;
            bits &= ~(0x7ffL << 52);
            bits |= 1022L << 52;
            return BitConverter.Int64BitsToDouble(bits);
        }

        public static float Float32FromBits(uint u)
        {
            var b = new F32Bits { U = u };
            return b.F;
        }

        public static uint Float32Bits(float f)
        {
            var b = new F32Bits { F = f };
            return b.U;
        }
    }
}
