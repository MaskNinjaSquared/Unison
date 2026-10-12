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
    }
}
