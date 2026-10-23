using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AliasFollowUpAvatarRowsTests
    {
        [Fact]
        public void Rows_with_an_avatar_are_skipped()
        {
            var withFace = new ChatItem { JID = "1@s.whatsapp.net", AvatarUrl = "ms-appdata:///local/a.jpg" };
            var blank = new ChatItem { JID = "1@s.whatsapp.net", AvatarUrl = null };

            List<ChatItem> rows = AliasFollowUpAvatarRows.CollectMissing(
                new[] { "1@s.whatsapp.net" },
                pn => new[] { withFace, blank });

            Assert.Single(rows);
            Assert.Same(blank, rows[0]);
        }

        [Fact]
        public void Null_or_empty_targets_yield_an_empty_list()
        {
            Assert.Empty(AliasFollowUpAvatarRows.CollectMissing(null, pn => new ChatItem[0]));
            Assert.Empty(AliasFollowUpAvatarRows.CollectMissing(new string[0], pn => new ChatItem[0]));
        }
    }
}
