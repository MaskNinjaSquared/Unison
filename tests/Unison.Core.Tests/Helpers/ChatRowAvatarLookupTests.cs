using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatRowAvatarLookupTests
    {
        private readonly List<ChatItem> _rows = new List<ChatItem>();
        private readonly Dictionary<string, string> _canonical =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private ChatRowIndex Index() =>
            new ChatRowIndex(() => _rows, jid =>
            {
                string key = JidHelper.Normalize(jid);
                string value;
                return key != null && _canonical.TryGetValue(key, out value) ? value : key;
            });

        private void AddRow(string jid, string avatarUrl) =>
            _rows.Add(new ChatItem { JID = jid, AvatarUrl = avatarUrl });

        [Fact]
        public void Find_returns_the_avatar_on_the_member_row()
        {
            AddRow("111@s.whatsapp.net", "a.jpg");

            Assert.Equal("a.jpg", ChatRowAvatarLookup.Find(Index(), "111@s.whatsapp.net", null, null));
        }

        [Fact]
        public void Find_skips_alias_rows_without_an_avatar()
        {
            _canonical["111@lid"] = "111@s.whatsapp.net";
            AddRow("111@lid", " ");
            AddRow("111@s.whatsapp.net", "a.jpg");

            Assert.Equal("a.jpg", ChatRowAvatarLookup.Find(Index(), "111@lid", null, null));
        }

        [Fact]
        public void Find_falls_back_to_the_phone_then_the_lid()
        {
            AddRow("999@lid", "lid.jpg");

            Assert.Equal("lid.jpg", ChatRowAvatarLookup.Find(Index(), "111@s.whatsapp.net", "222@s.whatsapp.net", "999@lid"));

            AddRow("222@s.whatsapp.net", "phone.jpg");

            Assert.Equal("phone.jpg", ChatRowAvatarLookup.Find(Index(), "111@s.whatsapp.net", "222@s.whatsapp.net", "999@lid"));
        }

        [Fact]
        public void Find_returns_null_when_no_row_has_one()
        {
            AddRow("111@s.whatsapp.net", null);

            Assert.Null(ChatRowAvatarLookup.Find(Index(), "111@s.whatsapp.net", " ", null));
            Assert.Null(ChatRowAvatarLookup.Find(Index(), " ", null, null));
        }
    }
}
