// =============================================================================
// Tests for AliasPairPolicy.
//
// Which LID/phone pairs are allowed into the alias table. The table is what says
// two addresses are one person, so a pair that gets through wrongly does not
// mislabel a row -- it merges two conversations, and the merge is not undone by
// the next message.
//
// The rule used to exist twice, once on the live path and once in the startup
// restore, and the two did not agree about the user's own dotted LID. That case
// has its own test below; it is the one that cost the user their own alias on
// every launch.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AliasPairPolicyTests
    {
        private const string SelfPn = "5511999999999@s.whatsapp.net";
        private const string SelfLid = "100200300@lid";
        private const string ContactPn = "5511888888888@s.whatsapp.net";
        private const string ContactLid = "400500600@lid";

        /// <summary>The user's own LID as some devices surface it: on the phone domain, dotted.</summary>
        private const string SelfLidDotted = "100200300.45@s.whatsapp.net";

        private const string GroupJid = "120363000000000000@g.us";

        private static JidAliasTable Bound(string? selfId = SelfPn, string? selfLid = SelfLid)
        {
            var table = new JidAliasTable();
            table.BindSelf(() => selfId!, () => selfLid!);
            return table;
        }

        // --- IsWellFormedPair: the shape -------------------------------------

        [Fact]
        public void A_lid_paired_with_a_phone_address_is_the_shape_we_file()
        {
            Assert.True(AliasPairPolicy.IsWellFormedPair(ContactLid, ContactPn, Bound()));
        }

        [Fact]
        public void The_two_sides_of_a_pair_are_not_interchangeable()
        {
            // The pair is filed both ways round, so a reversed one does not merely record
            // the same fact backwards: the phone address would then resolve to the LID, and
            // every row, avatar and name keyed on the phone address as the canonical one
            // would move to an address the user never sees.
            Assert.False(AliasPairPolicy.IsWellFormedPair(ContactPn, ContactLid, Bound()));
        }

        [Fact]
        public void A_lid_shaped_address_on_the_phone_domain_cannot_stand_on_the_phone_side()
        {
            // Some devices hand out LIDs on @s.whatsapp.net, dotted. Accepting one as the
            // phone side would make an internal identifier the canonical address of the
            // conversation, which is the same inversion as reversing the pair.
            Assert.False(AliasPairPolicy.IsWellFormedPair(ContactLid, "931777777.11@s.whatsapp.net", Bound()));
        }

        [Fact]
        public void A_lid_shaped_address_on_the_phone_domain_is_still_a_lid_on_the_lid_side()
        {
            // Read from the other side: the same address is welcome where a LID belongs.
            // Refusing it would drop the pairs those devices are the only source of.
            Assert.True(AliasPairPolicy.IsWellFormedPair(SelfLidDotted, ContactPn, Bound()));
        }

        [Fact]
        public void A_group_address_is_not_a_lid()
        {
            // Group jids travel through the same registration calls as contact pairs.
            Assert.False(AliasPairPolicy.IsWellFormedPair(GroupJid, ContactPn, Bound()));
        }

        [Theory]
        [InlineData(null, ContactPn)]
        [InlineData("", ContactPn)]
        [InlineData(ContactLid, null)]
        [InlineData(ContactLid, "")]
        public void A_pair_with_a_side_missing_is_not_a_pair(string? lid, string? pn)
        {
            Assert.False(AliasPairPolicy.IsWellFormedPair(lid, pn, Bound()));
        }

        [Fact]
        public void Without_a_table_no_pair_can_be_judged()
        {
            // The shape rules read the table to tell a LID from a phone number, so a
            // missing table has to fail closed rather than wave an unexamined pair through.
            Assert.False(AliasPairPolicy.IsWellFormedPair(ContactLid, ContactPn, null));
        }

        // --- WouldPutAContactUnderOurIdentity: poisoning ----------------------

        [Fact]
        public void A_contact_LID_cannot_be_filed_under_our_own_phone_address()
        {
            // This is the pair that merges a contact's conversation into the self chat:
            // from here on every message that contact sends lands in "message yourself",
            // and their own row stops receiving anything at all.
            Assert.True(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, SelfPn, Bound()));
        }

        [Fact]
        public void Our_own_LID_paired_with_our_own_phone_address_is_the_pair_that_must_survive()
        {
            // The only legitimate pair whose phone side is us. Dropping it would cost the
            // user the link between their two own addresses, and the self chat would split
            // in two the way a contact's chat does when the alias is unknown.
            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(SelfLid, SelfPn, Bound()));
        }

        [Fact]
        public void A_pair_between_two_contacts_is_none_of_this_rules_business()
        {
            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, ContactPn, Bound()));
        }

        [Fact]
        public void Our_own_LID_kept_in_its_dotted_form_is_still_recognised_as_ours()
        {
            // The case the two copies of this rule disagreed about. Our own LID arrives
            // after pairing, so before it does the only record that this dotted address is
            // ours is the reverse entry the table already holds -- and that entry is filed
            // under the plain LID. The live path reduced the dotted form before comparing;
            // the restore compared it as-is, never matched, and therefore threw away the
            // user's own legitimate alias on every single launch.
            var table = Bound(selfLid: null);
            table[SelfPn] = SelfLid;

            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(SelfLidDotted, SelfPn, table));
        }

        [Fact]
        public void A_reverse_entry_naming_a_different_LID_does_not_vouch_for_this_pair()
        {
            // Our phone address is already spoken for by another LID, so this one is not
            // the second half of our own identity -- it is a contact being filed under it.
            var table = Bound();
            table[SelfPn] = "777777777@lid";

            Assert.True(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, SelfPn, table));
        }

        [Fact]
        public void The_other_half_of_a_poisoned_pair_vouches_for_it()
        {
            // Recorded, not endorsed. Pairs are filed both ways, so a poisoned pair that
            // ever reached disk comes back as two entries. The half read here --
            // our phone address pointing at the contact's LID -- is not itself refused by
            // this rule, and once it is in the table it is indistinguishable from the
            // reverse entry that proves our own identity, so it validates the other half.
            //
            // Whether that happens depends on which of the two entries the restore loop
            // reaches first, which is dictionary order over the persisted file.
            var table = Bound();
            table[SelfPn] = ContactLid;

            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, SelfPn, table));
        }

        [Fact]
        public void Before_the_account_is_known_no_pair_can_be_poisoning_anything()
        {
            // Nothing is self-linked until the table is pointed at the logged-in account,
            // so the guard is inert until then and every pair reads as harmless. Callers
            // have to bind self before restoring aliases; WhatsAppService does it in its
            // constructor, over closures, so the binding survives the auth state being
            // loaded afterwards.
            var unbound = new JidAliasTable();

            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, SelfPn, unbound));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void A_pair_with_no_LID_at_all_is_still_reaching_for_our_identity(string? lid)
        {
            // Nothing can prove an absent LID is our own, so the phone side being us is
            // enough to refuse the pair. Erring the other way would file whatever wrote it
            // under the user's own address.
            Assert.True(AliasPairPolicy.WouldPutAContactUnderOurIdentity(lid, SelfPn, Bound()));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void With_no_phone_side_there_is_no_identity_to_be_filed_under(string? pn)
        {
            // This rule answers one question only; the shape rule is what refuses half a pair.
            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, pn, Bound()));
        }

        [Fact]
        public void Without_a_table_nothing_is_known_to_be_ours()
        {
            Assert.False(AliasPairPolicy.WouldPutAContactUnderOurIdentity(ContactLid, SelfPn, null));
        }

        // --- ReduceToPlainLid -------------------------------------------------

        [Theory]
        [InlineData("100200300.45@s.whatsapp.net")]
        [InlineData("100200300.45@S.WhatsApp.Net")]
        public void A_dotted_LID_on_the_phone_domain_reduces_to_the_key_the_table_files_it_under(string jid)
        {
            Assert.Equal(SelfLid, AliasPairPolicy.ReduceToPlainLid(jid, Bound()));
        }

        [Fact]
        public void A_plain_LID_has_no_instance_suffix_to_drop()
        {
            Assert.Equal(SelfLid, AliasPairPolicy.ReduceToPlainLid(SelfLid, Bound()));
        }

        [Theory]
        [InlineData(ContactPn)]
        [InlineData(GroupJid)]
        public void An_address_that_is_not_lid_shaped_is_returned_untouched(string jid)
        {
            // A phone number is the answer already. Rewriting one onto @lid would invent
            // an identity nobody is filed under and no lookup would ever match.
            Assert.Equal(jid, AliasPairPolicy.ReduceToPlainLid(jid, Bound()));
        }

        [Fact]
        public void An_address_with_nothing_before_the_dot_is_left_alone()
        {
            // A prefix has to be a LID. Taking the empty one would reduce any dotted
            // rubbish to the bare "@lid" domain, and two unrelated addresses reducing to
            // the same key is how the reverse-entry check comes to vouch for a pair it
            // has never seen.
            const string noPrefix = ".45@s.whatsapp.net";

            Assert.Equal(noPrefix, AliasPairPolicy.ReduceToPlainLid(noPrefix, Bound()));
        }

        [Fact]
        public void A_dotted_LID_on_the_lid_domain_is_left_for_the_caller_to_normalize()
        {
            // Documents where the reduction stops: it only reaches the phone domain, so a
            // dotted LID arriving on @lid comes back unchanged and would then be compared
            // against a normalized reverse entry that has the dot collapsed -- the exact
            // mismatch that used to discard the user's own alias. Both callers normalize
            // first, and JidHelper.Normalize collapses this form, so nothing reaches the
            // rule in this shape today. A caller that forgets brings the bug back.
            const string dottedLid = "100200300.45@lid";

            Assert.Equal(dottedLid, AliasPairPolicy.ReduceToPlainLid(dottedLid, Bound()));
            Assert.Equal(SelfLid, JidHelper.Normalize(dottedLid));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Nothing_reduces_to_something(string? jid)
        {
            Assert.Equal(jid, AliasPairPolicy.ReduceToPlainLid(jid, Bound()));
        }

        [Fact]
        public void Without_a_table_an_address_is_returned_as_it_came()
        {
            Assert.Equal(SelfLidDotted, AliasPairPolicy.ReduceToPlainLid(SelfLidDotted, null));
        }
    }
}
