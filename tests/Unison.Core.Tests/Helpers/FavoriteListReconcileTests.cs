using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FavoriteListReconcileTests
    {
        [Fact]
        public void ColdFavoritesToClear_returns_local_ids_missing_from_wanted()
        {
            var local = new[]
            {
                "111@s.whatsapp.net",
                "222@s.whatsapp.net",
                "333@s.whatsapp.net"
            };
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "111@s.whatsapp.net",
                "333@s.whatsapp.net"
            };

            IReadOnlyList<string> clear = FavoriteListReconcile.ColdFavoritesToClear(
                local,
                wanted,
                jid => jid);

            Assert.Single(clear);
            Assert.Equal("222@s.whatsapp.net", clear[0]);
        }

        [Fact]
        public void ColdFavoritesToClear_canonicalizes_and_dedupes()
        {
            var local = new[]
            {
                "111@s.whatsapp.net",
                "111@s.whatsapp.net",
                " 222@s.whatsapp.net "
            };
            var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IReadOnlyList<string> clear = FavoriteListReconcile.ColdFavoritesToClear(
                local,
                wanted,
                jid => (jid ?? string.Empty).Trim());

            Assert.Equal(2, clear.Count);
            Assert.Contains("111@s.whatsapp.net", clear);
            Assert.Contains("222@s.whatsapp.net", clear);
        }

        [Fact]
        public void ColdFavoritesToClear_null_inputs_return_empty()
        {
            Assert.Empty(FavoriteListReconcile.ColdFavoritesToClear(null, new HashSet<string>(), j => j));
            Assert.Empty(FavoriteListReconcile.ColdFavoritesToClear(new[] { "a" }, null, j => j));
            Assert.Empty(
                FavoriteListReconcile.ColdFavoritesToClear(
                    new[] { "a" },
                    new HashSet<string>(),
                    null));
        }
    }
}
