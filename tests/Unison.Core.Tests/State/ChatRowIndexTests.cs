// =============================================================================
// Tests for ChatRowIndex: the canonical-address lookup over the chat list,
// built on demand and dropped whenever the list or the aliases change.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ChatRowIndexTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "123456@lid";

        private static string Canonical(string jid) =>
            string.Equals(jid, Lid, StringComparison.OrdinalIgnoreCase) ? Pn : jid;

        [Fact]
        public void Rows_sharing_a_canonical_address_come_back_together()
        {
            var pnRow = new ChatItem { JID = Pn };
            var lidRow = new ChatItem { JID = Lid };
            var other = new ChatItem { JID = "x@s.whatsapp.net" };
            var index = new ChatRowIndex(() => new[] { pnRow, lidRow, other }, Canonical);

            List<ChatItem> rows = index.Find(Pn);

            Assert.Equal(new[] { pnRow, lidRow }, rows);
        }

        [Fact]
        public void The_key_is_case_insensitive()
        {
            var row = new ChatItem { JID = Pn };
            var index = new ChatRowIndex(() => new[] { row }, Canonical);

            Assert.Single(index.Find(Pn.ToUpperInvariant()));
        }

        [Fact]
        public void Unknown_or_blank_addresses_find_nothing()
        {
            var index = new ChatRowIndex(() => new[] { new ChatItem { JID = Pn } }, Canonical);

            Assert.Empty(index.Find("nobody@s.whatsapp.net"));
            Assert.Empty(index.Find(" "));
            Assert.Empty(index.Find(null));
        }

        [Fact]
        public void Null_rows_and_rows_without_an_address_are_left_out()
        {
            var index = new ChatRowIndex(
                () => new[] { null, new ChatItem { JID = null }, new ChatItem { JID = Pn } },
                Canonical);

            Assert.Single(index.Find(Pn));
        }

        [Fact]
        public void The_index_is_built_once_until_invalidated()
        {
            int builds = 0;
            var rows = new List<ChatItem> { new ChatItem { JID = Pn } };
            var index = new ChatRowIndex(() => { builds++; return rows; }, Canonical);

            index.Find(Pn);
            index.Find(Pn);
            Assert.Equal(1, builds);

            rows.Add(new ChatItem { JID = Lid });
            Assert.Single(index.Find(Pn));

            index.Invalidate();
            Assert.Equal(2, index.Find(Pn).Count);
            Assert.Equal(2, builds);
        }

        [Fact]
        public void A_build_that_raced_an_invalidation_is_not_kept()
        {
            int builds = 0;
            ChatRowIndex index = null;
            IEnumerable<ChatItem> Rows()
            {
                builds++;
                yield return new ChatItem { JID = Pn };
                if (builds == 1)
                {
                    index.Invalidate();
                }
            }

            index = new ChatRowIndex(Rows, Canonical);

            Assert.Single(index.Find(Pn));
            index.Find(Pn);

            Assert.Equal(2, builds);
        }

        [Fact]
        public void Finding_a_conversation_canonicalizes_the_address_first()
        {
            var pnRow = new ChatItem { JID = Pn };
            var lidRow = new ChatItem { JID = Lid };
            var index = new ChatRowIndex(() => new[] { pnRow, lidRow }, Canonical);

            Assert.Equal(new[] { pnRow, lidRow }, index.FindConversation(Lid));
            Assert.Equal(new[] { pnRow, lidRow }, index.FindConversation(Pn));
        }

        [Fact]
        public void Finding_a_blank_conversation_finds_nothing()
        {
            var index = new ChatRowIndex(() => new[] { new ChatItem { JID = Pn } }, Canonical);

            Assert.Empty(index.FindConversation(" "));
            Assert.Empty(index.FindConversation(null));
        }

        [Fact]
        public void Callers_get_a_copy_they_can_edit()
        {
            var index = new ChatRowIndex(() => new[] { new ChatItem { JID = Pn } }, Canonical);

            index.Find(Pn).Clear();

            Assert.Single(index.Find(Pn));
        }
    }
}
