// =============================================================================
// Tests for BackgroundDisplayNameTable.
//
// This table is what a toast reads while the app is not running. The precedence
// between its four sources used to be implied by the order of four loops and by
// which of them guarded with ContainsKey; these state it.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BackgroundDisplayNameTableTests
    {
        private const string AnaPn = "5511988888888@s.whatsapp.net";
        private const string AnaLid = "400500600@lid";

        private static ChatItem Chat(string jid, string name) =>
            new ChatItem { JID = jid, Name = name };

        private static Dictionary<string, string> Map(params string[] pairs)
        {
            var map = new Dictionary<string, string>();
            for (int i = 0; i < pairs.Length; i += 2)
            {
                map[pairs[i]] = pairs[i + 1];
            }

            return map;
        }

        private static Dictionary<string, string> Build(
            IEnumerable<ChatItem>? chats = null,
            Dictionary<string, string>? contactNames = null,
            Dictionary<string, string>? phoneContactNames = null,
            Dictionary<string, string>? aliases = null) =>
            BackgroundDisplayNameTable.Build(chats, contactNames, phoneContactNames, aliases);

        // --- sources ---------------------------------------------------------

        [Fact]
        public void A_chat_row_contributes_its_label()
        {
            var snapshot = Build(chats: new[] { Chat(AnaPn, "Ana") });

            Assert.Equal("Ana", snapshot[AnaPn]);
        }

        [Fact]
        public void A_contact_without_a_chat_row_is_still_included()
        {
            // Group participants need not have a conversation of their own.
            var snapshot = Build(contactNames: Map(AnaPn, "Ana"));

            Assert.Equal("Ana", snapshot[AnaPn]);
        }

        [Fact]
        public void Addresses_are_matched_regardless_of_case()
        {
            var snapshot = Build(chats: new[] { Chat(AnaPn, "Ana") });

            Assert.True(snapshot.ContainsKey(AnaPn.ToUpperInvariant()));
        }

        [Theory]
        [InlineData(null, "Ana")]
        [InlineData("", "Ana")]
        [InlineData("   ", "Ana")]
        [InlineData(AnaPn, null)]
        [InlineData(AnaPn, "")]
        [InlineData(AnaPn, "   ")]
        public void Half_an_entry_is_not_an_entry(string? jid, string? name)
        {
            Assert.Empty(Build(chats: new[] { Chat(jid!, name!) }));
        }

        [Fact]
        public void Nothing_in_means_an_empty_table_rather_than_a_crash()
        {
            Assert.Empty(Build());
        }

        // --- precedence ------------------------------------------------------

        [Fact]
        public void A_chat_label_beats_a_name_learned_from_WhatsApp()
        {
            var snapshot = Build(
                chats: new[] { Chat(AnaPn, "Ana") },
                contactNames: Map(AnaPn, "ana.souza93"));

            Assert.Equal("Ana", snapshot[AnaPn]);
        }

        [Fact]
        public void The_address_book_beats_the_chat_label()
        {
            // What the user called someone beats what they called themselves.
            var snapshot = Build(
                chats: new[] { Chat(AnaPn, "Ana") },
                phoneContactNames: Map(AnaPn, "Ana Souza (trabalho)"));

            Assert.Equal("Ana Souza (trabalho)", snapshot[AnaPn]);
        }

        [Fact]
        public void The_address_book_beats_a_name_learned_from_WhatsApp()
        {
            var snapshot = Build(
                contactNames: Map(AnaPn, "ana.souza93"),
                phoneContactNames: Map(AnaPn, "Ana Souza"));

            Assert.Equal("Ana Souza", snapshot[AnaPn]);
        }

        // --- alias mirroring -------------------------------------------------

        [Fact]
        public void A_name_known_under_one_identity_form_is_mirrored_onto_the_other()
        {
            var snapshot = Build(
                chats: new[] { Chat(AnaPn, "Ana") },
                aliases: Map(AnaLid, AnaPn));

            Assert.Equal("Ana", snapshot[AnaLid]);
            Assert.Equal("Ana", snapshot[AnaPn]);
        }

        [Fact]
        public void Mirroring_works_in_the_other_direction_too()
        {
            var snapshot = Build(
                chats: new[] { Chat(AnaLid, "Ana") },
                aliases: Map(AnaLid, AnaPn));

            Assert.Equal("Ana", snapshot[AnaPn]);
        }

        [Fact]
        public void Mirroring_never_overwrites_a_name_that_is_already_there()
        {
            // Both halves already named means something with more authority than an
            // alias named them.
            var snapshot = Build(
                chats: new[] { Chat(AnaPn, "Ana"), Chat(AnaLid, "Ana (LID)") },
                aliases: Map(AnaLid, AnaPn));

            Assert.Equal("Ana", snapshot[AnaPn]);
            Assert.Equal("Ana (LID)", snapshot[AnaLid]);
        }

        [Fact]
        public void An_alias_between_two_unknown_addresses_invents_nothing()
        {
            var snapshot = Build(aliases: Map(AnaLid, AnaPn));

            Assert.Empty(snapshot);
        }

        [Fact]
        public void A_mirrored_name_comes_from_the_address_book_when_there_is_one()
        {
            // Mirroring runs last, so it carries whatever won the earlier rounds.
            var snapshot = Build(
                chats: new[] { Chat(AnaPn, "Ana") },
                phoneContactNames: Map(AnaPn, "Ana Souza"),
                aliases: Map(AnaLid, AnaPn));

            Assert.Equal("Ana Souza", snapshot[AnaLid]);
        }
    }
}
