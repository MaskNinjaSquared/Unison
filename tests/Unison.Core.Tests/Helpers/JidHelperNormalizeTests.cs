// =============================================================================
// Tests for JidHelper.Normalize.
//
// This is the function that decides whether two addresses are the same
// conversation. Everything downstream inherits its answer: which row a message
// lands in, whether the list shows one chat or two, which history keys are
// queried. It had no tests.
//
// These are characterization tests -- they record what it does today, so a
// later change to chat identity has to be deliberate. Where the behaviour looks
// questionable it is marked as recorded rather than endorsed, not corrected
// here.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class JidHelperNormalizeTests
    {
        // --- Nothing to do ----------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Nothing_in_nothing_out(string? jid)
        {
            Assert.Equal(jid, JidHelper.Normalize(jid));
        }

        [Fact]
        public void Surrounding_whitespace_is_always_removed()
        {
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize("  5511988887777@s.whatsapp.net  "));
        }

        [Theory]
        [InlineData("not-a-jid")]
        [InlineData("@s.whatsapp.net")]
        [InlineData("5511988887777@")]
        public void Something_that_is_not_an_address_is_left_alone(string? jid)
        {
            // No separator, nothing before it, or nothing after it. Returned untouched
            // rather than guessed at.
            Assert.Equal(jid, JidHelper.Normalize(jid));
        }

        // --- Direct chats -----------------------------------------------------

        [Fact]
        public void A_plain_address_survives_unchanged()
        {
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize("5511988887777@s.whatsapp.net"));
        }

        [Fact]
        public void The_server_part_is_lowercased_so_case_cannot_split_a_conversation()
        {
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize("5511988887777@S.WhatsApp.NET"));
        }

        [Fact]
        public void A_device_suffix_is_dropped_so_every_device_is_one_person()
        {
            // The same contact writing from phone and from desktop must not become two chats.
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize("5511988887777:12@s.whatsapp.net"));
        }

        [Fact]
        public void A_device_suffix_and_a_shouting_server_are_handled_together()
        {
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize(" 5511988887777:3@S.WHATSAPP.NET "));
        }

        // --- LIDs --------------------------------------------------------------

        [Fact]
        public void A_lid_loses_its_instance_suffix()
        {
            Assert.Equal("199999999999@lid", JidHelper.Normalize("199999999999.5@lid"));
        }

        [Fact]
        public void A_lid_with_several_dots_keeps_only_what_precedes_the_first()
        {
            Assert.Equal("199999999999@lid", JidHelper.Normalize("199999999999.5.1@lid"));
        }

        [Fact]
        public void A_plain_lid_is_untouched()
        {
            Assert.Equal("199999999999@lid", JidHelper.Normalize("199999999999@lid"));
        }

        // --- LID-shaped addresses on the contact server -------------------------

        [Fact]
        public void A_dotted_zero_alias_collapses()
        {
            // Some device addresses arrive as "<user>.0". That zero carries nothing.
            Assert.Equal("5511988887777@s.whatsapp.net", JidHelper.Normalize("5511988887777.0@s.whatsapp.net"));
        }

        [Fact]
        public void A_dotted_identifier_that_is_not_zero_is_kept_whole()
        {
            // Unlike a LID, this is not an instance suffix: dropping it would merge two
            // different identities into one conversation.
            Assert.Equal("5511988887777.5@s.whatsapp.net", JidHelper.Normalize("5511988887777.5@s.whatsapp.net"));
        }

        [Fact]
        public void Only_the_final_segment_is_examined_for_the_zero()
        {
            Assert.Equal("5511988887777.5@s.whatsapp.net", JidHelper.Normalize("5511988887777.5.0@s.whatsapp.net"));
        }

        // --- Groups -------------------------------------------------------------

        [Fact]
        public void A_group_address_survives_unchanged()
        {
            Assert.Equal("120363000000000000@g.us", JidHelper.Normalize("120363000000000000@g.us"));
        }

        [Fact]
        public void A_group_address_is_returned_before_any_other_rule_runs()
        {
            // RECORDED, NOT ENDORSED. Groups take an early return, so unlike a direct
            // address the server part is never lowercased. Two spellings of the same group
            // therefore normalize to two different strings.
            //
            // That is only harmless while every consumer compares group addresses
            // case-insensitively. WhatsAppService does; ChatStateStore does not -- it keys
            // chats and messages with StringComparer.Ordinal. If a mixed-case @g.us ever
            // reaches the store, one group becomes two rows.
            //
            // Left as-is: changing chat identity is not a change to make without a device
            // pass. This test exists so the asymmetry is declared rather than discovered.
            Assert.Equal("120363000000000000@G.US", JidHelper.Normalize("120363000000000000@G.US"));
        }

        [Fact]
        public void A_group_address_is_still_trimmed()
        {
            // Trimming happens before the early return, so this much is shared.
            Assert.Equal("120363000000000000@g.us", JidHelper.Normalize("  120363000000000000@g.us  "));
        }

        // --- Idempotence ----------------------------------------------------------

        [Theory]
        [InlineData("5511988887777:12@S.WhatsApp.NET")]
        [InlineData("199999999999.5@lid")]
        [InlineData("5511988887777.0@s.whatsapp.net")]
        [InlineData("120363000000000000@g.us")]
        public void Normalizing_an_already_normalized_address_changes_nothing(string? jid)
        {
            // Load-bearing: the same address is normalized repeatedly as it moves between
            // the socket, the store and the list. A second pass that altered it would make
            // identity depend on how many times the value had been handled.
            string once = JidHelper.Normalize(jid);

            Assert.Equal(once, JidHelper.Normalize(once));
        }

        // --- Related helpers, which all normalize first -----------------------------

        [Fact]
        public void A_phone_number_is_read_back_out_of_a_contact_address()
        {
            Assert.Equal("5511988887777", JidHelper.TryPhoneFromJid("5511988887777:9@S.WhatsApp.net"));
        }

        [Fact]
        public void There_is_no_phone_number_to_read_out_of_a_group_or_a_lid()
        {
            Assert.Null(JidHelper.TryPhoneFromJid("120363000000000000@g.us"));
            Assert.Null(JidHelper.TryPhoneFromJid("199999999999@lid"));
        }

        [Fact]
        public void The_status_feed_is_recognised_however_it_is_written()
        {
            Assert.True(JidHelper.IsStatusBroadcast("status@broadcast"));
            Assert.True(JidHelper.IsStatusBroadcast("  STATUS@BROADCAST  "));
            Assert.False(JidHelper.IsStatusBroadcast("5511988887777@s.whatsapp.net"));
        }
    }

    public class JidHelperSuffixTests
    {
        // The two halves of a PN/LID pair. They are asked together to sort a pair into its
        // sides, so a casing disagreement between them files the same address twice.

        [Theory]
        [InlineData("100200300@lid")]
        [InlineData("100200300@LID")]
        [InlineData("100200300@Lid")]
        public void A_LID_is_recognised_whatever_case_the_server_used(string jid)
        {
            Assert.True(JidHelper.IsLidJid(jid));
            Assert.False(JidHelper.IsPhoneJid(jid));
        }

        [Theory]
        [InlineData("5511999999999@s.whatsapp.net")]
        [InlineData("5511999999999@S.WHATSAPP.NET")]
        [InlineData("5511999999999@s.WhatsApp.net")]
        public void A_number_is_recognised_whatever_case_the_server_used(string jid)
        {
            Assert.True(JidHelper.IsPhoneJid(jid));
            Assert.False(JidHelper.IsLidJid(jid));
        }

        [Theory]
        [InlineData("120363000000000000@g.us")]
        [InlineData("120363000000000000@G.US")]
        [InlineData("status@broadcast")]
        [InlineData("")]
        [InlineData(null)]
        public void Anything_else_belongs_to_neither_side(string? jid)
        {
            Assert.False(JidHelper.IsLidJid(jid!));
            Assert.False(JidHelper.IsPhoneJid(jid!));
        }

        [Theory]
        [InlineData("100200300@LID")]
        [InlineData("5511999999999@S.WHATSAPP.NET")]
        public void A_pair_never_sorts_the_same_address_onto_both_sides(string oddlyCased)
        {
            // The bug this replaced: the odd-cased address matched neither suffix, so both
            // sides fell through to the alias and the merge scan compared a chat to itself.
            const string alias = "other@s.whatsapp.net";

            string lid = JidHelper.IsLidJid(oddlyCased) ? oddlyCased : alias;
            string pn = JidHelper.IsPhoneJid(oddlyCased) ? oddlyCased : alias;

            Assert.NotEqual(lid, pn);
        }
    }
}
