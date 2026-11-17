using System;
using System.IO;
using System.Text.Json;

namespace Unison.Core.Tests.Voip.Crypto
{
    internal static class CryptoKats
    {
        public static string ReadText(params string[] relative)
        {
            var parts = new string[relative.Length + 3];
            parts[0] = AppContext.BaseDirectory;
            parts[1] = "Voip";
            parts[2] = "TestData";
            Array.Copy(relative, 0, parts, 3, relative.Length);
            return File.ReadAllText(Path.Combine(parts));
        }

        public static JsonElement LoadSrtp()
        {
            using (var doc = JsonDocument.Parse(ReadText("srtp", "kats.json")))
            {
                return doc.RootElement.Clone();
            }
        }

        public static string Str(this JsonElement root, string section, string field)
        {
            return root.GetProperty(section).GetProperty(field).GetString();
        }

        public static byte[] HexOf(this JsonElement root, string section, string field)
        {
            return Hex(root.Str(section, field));
        }

        public static byte[] Hex(string s)
        {
            return Convert.FromHexString(s);
        }

        public static string ToHex(byte[] b)
        {
            return Convert.ToHexString(b).ToLowerInvariant();
        }
    }
}
