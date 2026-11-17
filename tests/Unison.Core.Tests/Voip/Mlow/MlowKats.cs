using System;
using System.IO;
using System.Text.Json;

namespace Unison.Core.Tests.Voip.Mlow
{
    internal static class MlowKats
    {
        public static string PathOf(string name)
        {
            return Path.Combine(AppContext.BaseDirectory, "Voip", "TestData", "mlow", name);
        }

        public static byte[] ReadBytes(string name)
        {
            return File.ReadAllBytes(PathOf(name));
        }

        public static JsonElement LoadJson(string name)
        {
            using (var doc = JsonDocument.Parse(File.ReadAllText(PathOf(name))))
            {
                return doc.RootElement.Clone();
            }
        }

        public static byte[] Hex(string s)
        {
            return Convert.FromHexString(s);
        }

        public static int[] Ints(this JsonElement e)
        {
            var o = new int[e.GetArrayLength()];
            int i = 0;
            foreach (var x in e.EnumerateArray())
            {
                o[i++] = x.GetInt32();
            }

            return o;
        }

        public static float[] Floats(this JsonElement e)
        {
            var o = new float[e.GetArrayLength()];
            int i = 0;
            foreach (var x in e.EnumerateArray())
            {
                o[i++] = x.GetSingle();
            }

            return o;
        }

        public static ushort[] UShorts(this JsonElement e)
        {
            var o = new ushort[e.GetArrayLength()];
            int i = 0;
            foreach (var x in e.EnumerateArray())
            {
                o[i++] = x.GetUInt16();
            }

            return o;
        }

        public static string[] Strings(this JsonElement e)
        {
            var o = new string[e.GetArrayLength()];
            int i = 0;
            foreach (var x in e.EnumerateArray())
            {
                o[i++] = x.GetString() ?? string.Empty;
            }

            return o;
        }

        public static uint F32Ulp(float a, float b)
        {
            uint ia = BitConverter.SingleToUInt32Bits(a);
            uint ib = BitConverter.SingleToUInt32Bits(b);
            return ia > ib ? ia - ib : ib - ia;
        }
    }
}
