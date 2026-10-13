using System.Collections.Generic;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// A typed number is already known when it is the start of an alias or of a stored name key.
    /// Alias pairs are asked first, and the first hit wins.
    /// </summary>
    public class KnownJidByPhoneDigitsTests
    {
        [Fact]
        public void Blank_digits_match_nothing()
        {
            Assert.Null(KnownJidByPhoneDigits.Find(null, Pairs("1@s.whatsapp.net", "a"), Names("1@s.whatsapp.net")));
            Assert.Null(KnownJidByPhoneDigits.Find("", Pairs("1@s.whatsapp.net", "a"), Names("1@s.whatsapp.net")));
        }

        [Fact]
        public void An_alias_key_wins_before_its_value_and_before_a_name()
        {
            string found = KnownJidByPhoneDigits.Find(
                "5511",
                Pairs("5511988887777@s.whatsapp.net", "5511977776666@lid"),
                Names("5511900000000@s.whatsapp.net"));

            Assert.Equal("5511988887777@s.whatsapp.net", found);
        }

        [Fact]
        public void An_alias_value_is_used_when_the_key_does_not_match()
        {
            string found = KnownJidByPhoneDigits.Find(
                "999",
                Pairs("111@lid", "999@s.whatsapp.net"),
                Names("111@s.whatsapp.net"));

            Assert.Equal("999@s.whatsapp.net", found);
        }

        [Fact]
        public void A_stored_name_is_used_only_after_the_aliases()
        {
            string found = KnownJidByPhoneDigits.Find(
                "44",
                Pairs("111@lid", "222@s.whatsapp.net"),
                Names("441234@s.whatsapp.net"));

            Assert.Equal("441234@s.whatsapp.net", found);
        }

        [Fact]
        public void Nothing_known_returns_null()
        {
            Assert.Null(KnownJidByPhoneDigits.Find("55", null, null));
        }

        private static IEnumerable<KeyValuePair<string, string>> Pairs(string key, string value)
        {
            yield return new KeyValuePair<string, string>(key, value);
        }

        private static IEnumerable<string> Names(string jid)
        {
            yield return jid;
        }
    }
}
