using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatPinStoreKeysTests
    {
        [Fact]
        public void For_includes_jid_canonical_and_alias()
        {
            var jids = new FakeJids();
            jids.Canonical["111@lid"] = "111@s.whatsapp.net";
            jids.Alias["111@lid"] = "111@s.whatsapp.net";
            jids.Alias["111@s.whatsapp.net"] = "111@lid";

            IReadOnlyList<string> keys = ChatPinStoreKeys.For("111@lid", jids);

            Assert.Contains("111@lid", keys);
            Assert.Contains("111@s.whatsapp.net", keys);
            Assert.Equal(2, keys.Count);
        }

        [Fact]
        public void For_finds_the_alias_of_a_device_suffixed_address()
        {
            // The table stores normalized keys. A resolver matches what it is given, so the
            // helper has to normalize before asking — otherwise a ":device" address misses
            // the pair and the pin is written under one spelling only.
            var jids = new StrictJids();
            jids.Alias["111@s.whatsapp.net"] = "999@lid";

            IReadOnlyList<string> keys = ChatPinStoreKeys.For("111:8@s.whatsapp.net", jids);

            Assert.Contains("111@s.whatsapp.net", keys);
            Assert.Contains("999@lid", keys);
        }

        [Fact]
        public void For_explicit_parts_collapses_case_device_suffix_and_duplicates()
        {
            IReadOnlyList<string> keys = ChatPinStoreKeys.For(
                " 111:8@S.WhatsApp.net ",
                "111@s.whatsapp.net",
                "999@lid");

            Assert.Equal(new[] { "111@s.whatsapp.net", "999@lid" }, keys);
        }

        private sealed class FakeJids : IJidResolver
        {
            public Dictionary<string, string> Canonical { get; } =
                new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            public Dictionary<string, string> Alias { get; } =
                new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public string GetCanonicalJid(string jid)
            {
                string key = JidHelper.Normalize(jid);
                string value;
                return Canonical.TryGetValue(key, out value) ? value : key;
            }

            public bool TryGetAlias(string jid, out string alias)
            {
                return Alias.TryGetValue(JidHelper.Normalize(jid) ?? string.Empty, out alias);
            }

            public bool IsSelfLinked(string jid) => false;
        }

        /// <summary>Matches <see cref="IJidResolver"/> as specified: the key is used as given.</summary>
        private sealed class StrictJids : IJidResolver
        {
            public Dictionary<string, string> Alias { get; } =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => jid;

            public bool TryGetAlias(string jid, out string alias)
            {
                if (jid == null)
                {
                    alias = null;
                    return false;
                }

                return Alias.TryGetValue(jid, out alias);
            }

            public bool IsSelfLinked(string jid) => false;
        }
    }
}
