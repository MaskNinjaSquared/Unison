// =============================================================================
// Tests for ActiveChat: which conversation the user has open, asked from any
// alias, and the change announced to whoever keeps per-conversation memory.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class ActiveChatTests
    {
        private const string Pn = "5511999990001@s.whatsapp.net";
        private const string Lid = "123456@lid";
        private const string Other = "5511999990002@s.whatsapp.net";

        private static string Canonical(string jid) =>
            string.Equals(jid, Lid, StringComparison.OrdinalIgnoreCase) ? Pn : jid;

        [Fact]
        public void Nothing_is_open_at_first()
        {
            var active = new ActiveChat(Canonical);

            Assert.Null(active.Jid);
            Assert.False(active.IsOpen(Pn));
        }

        [Fact]
        public void The_open_conversation_is_held_by_its_canonical_address()
        {
            var active = new ActiveChat(Canonical);

            active.Open(Lid);

            Assert.Equal(Pn, active.Jid);
        }

        [Fact]
        public void Any_alias_of_the_open_conversation_counts_as_open()
        {
            var active = new ActiveChat(Canonical);

            active.Open(Pn);

            Assert.True(active.IsOpen(Lid));
            Assert.True(active.IsOpen(Pn.ToUpperInvariant()));
            Assert.False(active.IsOpen(Other));
        }

        [Fact]
        public void Blank_addresses_are_never_open()
        {
            var active = new ActiveChat(Canonical);
            active.Open(Pn);

            Assert.False(active.IsOpen(null));
            Assert.False(active.IsOpen(" "));
        }

        [Fact]
        public void Opening_a_blank_address_closes_the_conversation()
        {
            var active = new ActiveChat(Canonical);
            active.Open(Pn);

            active.Open(" ");

            Assert.Null(active.Jid);
            Assert.False(active.IsOpen(Pn));
        }

        [Fact]
        public void Every_open_announces_the_previous_and_current_conversation()
        {
            var active = new ActiveChat(Canonical);
            var seen = new List<ActiveChatChangedEventArgs>();
            active.Changed += (_, e) => seen.Add(e);

            active.Open(Pn);
            active.Open(Lid);
            active.Open(Other);
            active.Open(null);

            Assert.Equal(4, seen.Count);
            Assert.Null(seen[0].Previous);
            Assert.Equal(Pn, seen[0].Current);
            Assert.Equal(Pn, seen[1].Previous);
            Assert.Equal(Pn, seen[1].Current);
            Assert.Equal(Pn, seen[2].Previous);
            Assert.Equal(Other, seen[2].Current);
            Assert.Equal(Other, seen[3].Previous);
            Assert.Null(seen[3].Current);
        }

        [Fact]
        public void The_new_conversation_is_already_open_when_the_change_is_announced()
        {
            var active = new ActiveChat(Canonical);
            bool openDuringEvent = false;
            active.Changed += (_, e) => openDuringEvent = active.IsOpen(Other);

            active.Open(Other);

            Assert.True(openDuringEvent);
        }

        [Fact]
        public void A_missing_canonical_lookup_is_rejected()
        {
            Assert.Throws<ArgumentNullException>(() => new ActiveChat(null));
        }
    }
}
