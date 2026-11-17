using System;
using System.Text.Json;
using Unison.Voip.Engine.Crypto;
using Xunit;

namespace Unison.Core.Tests.Voip.Crypto
{
    public sealed class HkdfTests
    {
        [Fact]
        public void HkdfSha256_matches_rfc5869_vectors()
        {
            using (var doc = JsonDocument.Parse(CryptoKats.ReadText("util", "rfc5869_hkdf_sha256.json")))
            {
                var recs = doc.RootElement;
                Assert.True(recs.GetArrayLength() > 0, "no hkdf vectors");

                foreach (var rec in recs.EnumerateArray())
                {
                    string name = rec.GetProperty("name").GetString();
                    byte[] ikm = CryptoKats.Hex(rec.GetProperty("ikm").GetString());
                    byte[] salt = CryptoKats.Hex(rec.GetProperty("salt").GetString());
                    byte[] info = CryptoKats.Hex(rec.GetProperty("info").GetString());
                    int length = rec.GetProperty("l").GetInt32();
                    string want = rec.GetProperty("okm").GetString().ToLowerInvariant();

                    byte[] got = Hkdf.Sha256(salt, ikm, info, length);

                    Assert.True(got.Length == length, name + ": length mismatch");
                    Assert.Equal(want, CryptoKats.ToHex(got));
                }
            }
        }

        [Fact]
        public void HkdfSha256_rejects_length_over_rfc_bound()
        {
            Assert.Throws<ArgumentException>(() => Hkdf.Sha256(null, new byte[32], null, 255 * 32 + 1));
            Assert.Equal(255 * 32, Hkdf.Sha256(null, new byte[32], null, 255 * 32).Length);
        }
    }
}
