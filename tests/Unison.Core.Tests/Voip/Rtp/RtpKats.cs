using System;
using System.IO;
using System.Text.Json;

namespace Unison.Core.Tests.Voip.Rtp
{
    internal static class RtpKats
    {
        private static readonly Lazy<JsonDocument> Document = new Lazy<JsonDocument>(() =>
            JsonDocument.Parse(File.ReadAllText(
                Path.Combine(AppContext.BaseDirectory, "Voip", "TestData", "rtp", "kats.json"))));

        public static JsonElement Root => Document.Value.RootElement;

        public static string Str(string section, string name) =>
            Root.GetProperty(section).GetProperty(name).GetString()!;

        public static uint U32(string section, string name) =>
            Root.GetProperty(section).GetProperty(name).GetUInt32();

        public static ulong U64(string section, string name) =>
            Root.GetProperty(section).GetProperty(name).GetUInt64();

        public static byte[] Hex(string hex) => Convert.FromHexString(hex);

        public static string ToHex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
