// =============================================================================
// Tests for ChatListAddressBookTargets.
//
// Which chat-list rows the startup address-book pass looks up: 1:1 chats only,
// once each, under the address their saved name is filed under.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListAddressBookTargetsTests
    {
        private const string Ana = "5511988887777@s.whatsapp.net";
        private const string AnaLid = "123456789012345@lid";
        private const string Bruno = "5511977776666@s.whatsapp.net";
        private const string Group = "120363000000000000@g.us";

        private static readonly Func<string, string> NoAliases = jid => jid;

        private static ChatItem Chat(string jid, bool isGroup = false) =>
            new ChatItem { JID = jid, IsGroup = isGroup };

        [Fact]
        public void Direct_chats_are_looked_up()
        {
            List<string> targets = ChatListAddressBookTargets.Select(
                new[] { Chat(Ana), Chat(Bruno) }, NoAliases);

            Assert.Equal(new[] { Ana, Bruno }, targets);
        }

        [Fact]
        public void Groups_are_skipped()
        {
            List<string> targets = ChatListAddressBookTargets.Select(
                new[] { Chat(Group, isGroup: true), Chat(Ana) }, NoAliases);

            Assert.Equal(new[] { Ana }, targets);
        }

        [Fact]
        public void A_group_address_is_skipped_even_when_the_row_is_not_flagged()
        {
            // Rows restored before their metadata arrived can carry a group JID without the flag.
            List<string> targets = ChatListAddressBookTargets.Select(new[] { Chat(Group) }, NoAliases);

            Assert.Empty(targets);
        }

        [Fact]
        public void A_LID_chat_is_looked_up_under_its_phone_address()
        {
            Func<string, string> aliases = jid => jid == AnaLid ? Ana : jid;

            List<string> targets = ChatListAddressBookTargets.Select(new[] { Chat(AnaLid) }, aliases);

            Assert.Equal(new[] { Ana }, targets);
        }

        [Fact]
        public void The_same_person_under_two_rows_is_looked_up_once()
        {
            Func<string, string> aliases = jid => jid == AnaLid ? Ana : jid;

            List<string> targets = ChatListAddressBookTargets.Select(
                new[] { Chat(Ana), Chat(AnaLid) }, aliases);

            Assert.Equal(new[] { Ana }, targets);
        }

        [Fact]
        public void Rows_without_an_address_are_ignored()
        {
            List<string> targets = ChatListAddressBookTargets.Select(
                new[] { null, Chat(null), Chat("  "), Chat(Ana) }, NoAliases);

            Assert.Equal(new[] { Ana }, targets);
        }

        [Fact]
        public void An_alias_lookup_that_finds_nothing_keeps_the_rows_own_address()
        {
            List<string> targets = ChatListAddressBookTargets.Select(new[] { Chat(Ana) }, jid => null);

            Assert.Equal(new[] { Ana }, targets);
        }
    }
}
