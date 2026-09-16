// =============================================================================
// Characterization tests for JidAliasTable.
//
// Canonicalization decides which row a message lands on. Phase 3.9b moves the
// code that persists and reconciles those rows, and every key it uses comes
// from here, so this is the part of the net that matters most.
// =============================================================================
using System;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class JidAliasTableTests
    {
        private const string SelfPn = "5511999999999@s.whatsapp.net";
        private const string SelfLid = "100200300@lid";
        private const string ContactPn = "5511888888888@s.whatsapp.net";
        private const string ContactLid = "400500600@lid";

        private static JidAliasTable Bound(string? selfId = SelfPn, string? selfLid = SelfLid)
        {
            var table = new JidAliasTable();
            table.BindSelf(() => selfId!, () => selfLid!);
            return table;
        }

        /// <summary>Both directions, the way TryRecordAliasMapping writes them.</summary>
        private static void Pair(JidAliasTable table, string lid, string pn)
        {
            table[lid] = pn;
            table[pn] = lid;
        }

        // --- GetCanonicalJid -------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void Empty_input_is_returned_as_is(string? jid)
        {
            Assert.Equal(jid, Bound().GetCanonicalJid(jid));
        }

        [Fact]
        public void An_unknown_address_is_returned_normalized()
        {
            Assert.Equal(ContactPn, Bound().GetCanonicalJid("  5511888888888@S.WhatsApp.Net  "));
        }

        [Fact]
        public void A_device_suffix_is_dropped()
        {
            Assert.Equal(ContactPn, Bound().GetCanonicalJid("5511888888888:12@s.whatsapp.net"));
        }

        [Fact]
        public void A_known_lid_canonicalizes_to_the_phone_address()
        {
            var table = Bound();
            Pair(table, ContactLid, ContactPn);

            Assert.Equal(ContactPn, table.GetCanonicalJid(ContactLid));
        }

        [Fact]
        public void A_known_phone_address_stays_itself()
        {
            var table = Bound();
            Pair(table, ContactLid, ContactPn);

            Assert.Equal(ContactPn, table.GetCanonicalJid(ContactPn));
        }

        [Fact]
        public void Between_two_phone_addresses_the_non_instance_form_wins()
        {
            // Some devices surface LID-shaped ids on @s.whatsapp.net, dotted.
            const string lidLikePn = "931777777.11@s.whatsapp.net";
            var table = Bound();
            table[lidLikePn] = ContactPn;
            table[ContactPn] = lidLikePn;

            Assert.Equal(ContactPn, table.GetCanonicalJid(lidLikePn));
            Assert.Equal(ContactPn, table.GetCanonicalJid(ContactPn));
        }

        [Fact]
        public void A_dotted_phone_address_resolves_through_its_base_lid()
        {
            var table = Bound();
            Pair(table, "400500600@lid", ContactPn);

            Assert.Equal(ContactPn, table.GetCanonicalJid("400500600.7@s.whatsapp.net"));
        }

        [Fact]
        public void Our_own_address_canonicalizes_to_our_phone_address()
        {
            var table = Bound();

            Assert.Equal(SelfPn, table.GetCanonicalJid(SelfLid));
        }

        // --- self-poisoning --------------------------------------------------

        [Fact]
        public void An_alias_pointing_a_contact_at_self_is_ignored_when_self_is_only_self_linked()
        {
            // The guard fires when the target is self-linked but is not literally
            // our id or lid - here, a device-suffixed form of our own number.
            var table = Bound();
            table[ContactLid] = "5511999999999.3@s.whatsapp.net";

            Assert.Equal(ContactLid, table.GetCanonicalJid(ContactLid));
        }

        [Fact]
        public void A_contact_aliased_straight_to_our_id_is_NOT_caught_by_this_guard()
        {
            // Recorded, not endorsed. The comment on the guard reads "never canonicalize
            // a non-self contact to our own JID", but it cannot fire for this case:
            // IsSelfLinked(ContactLid) consults the very alias being validated and so
            // reports true, which switches the guard off.
            //
            // The protection is upstream, in the two paths that write this table:
            // WhatsAppService.TryRecordAliasMapping refuses such a pair on the live
            // path, and the startup restore in WhatsAppService.Connection.cs now drops
            // it via IsSelfPoisoningAliasPair. Both ask before inserting, which is what
            // makes the answer meaningful - asked afterwards, as here, the entry is its
            // own alibi.
            var table = Bound();
            table[ContactLid] = SelfPn;

            Assert.Equal(SelfPn, table.GetCanonicalJid(ContactLid));
        }

        [Fact]
        public void A_bidirectional_self_alias_is_allowed_through()
        {
            // Our own LID paired with our own phone address is a legitimate pair.
            var table = Bound();
            Pair(table, SelfLid, SelfPn);

            Assert.Equal(SelfPn, table.GetCanonicalJid(SelfLid));
        }

        // --- IsSelfLinked ----------------------------------------------------

        [Fact]
        public void Nothing_is_self_linked_before_the_account_is_known()
        {
            var table = new JidAliasTable();

            Assert.False(table.IsSelfLinked(SelfPn));
            Assert.False(table.IsSelfLinked(ContactPn));
        }

        [Fact]
        public void Our_id_and_our_lid_are_both_self_linked()
        {
            var table = Bound();

            Assert.True(table.IsSelfLinked(SelfPn));
            Assert.True(table.IsSelfLinked(SelfLid));
        }

        [Fact]
        public void A_contact_is_not_self_linked()
        {
            Assert.False(Bound().IsSelfLinked(ContactPn));
        }

        [Fact]
        public void A_device_suffixed_form_of_our_number_is_self_linked()
        {
            Assert.True(Bound().IsSelfLinked("5511999999999:9@s.whatsapp.net"));
        }

        [Fact]
        public void A_dotted_address_whose_base_lid_is_ours_is_self_linked()
        {
            var table = Bound();
            table["777888999@lid"] = SelfPn;

            Assert.True(table.IsSelfLinked("777888999.4@s.whatsapp.net"));
        }

        // --- IsLidLike -------------------------------------------------------

        [Theory]
        [InlineData("400500600@lid", true)]
        [InlineData("931777777.11@s.whatsapp.net", true)]
        [InlineData("5511888888888@s.whatsapp.net", false)]
        [InlineData("120363000000000000@g.us", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Lid_shaped_addresses_are_recognised(string? jid, bool expected)
        {
            Assert.Equal(expected, Bound().IsLidLike(jid));
        }

        // --- GetCanonicalSelfPnJid -------------------------------------------

        [Fact]
        public void Our_phone_address_is_preferred_when_we_have_one()
        {
            Assert.Equal(SelfPn, Bound().GetCanonicalSelfPnJid());
        }

        [Fact]
        public void Our_phone_address_is_looked_up_through_our_lid_when_the_id_is_a_lid()
        {
            var table = Bound(selfId: SelfLid, selfLid: SelfLid);
            table[SelfLid] = SelfPn;

            Assert.Equal(SelfPn, table.GetCanonicalSelfPnJid());
        }

        [Fact]
        public void Our_lid_is_the_answer_of_last_resort()
        {
            var table = Bound(selfId: null, selfLid: SelfLid);

            Assert.Equal(SelfLid, table.GetCanonicalSelfPnJid());
        }

        [Fact]
        public void There_is_no_self_address_before_pairing()
        {
            var table = Bound(selfId: null, selfLid: null);

            Assert.Null(table.GetCanonicalSelfPnJid());
        }

        // --- Changed ---------------------------------------------------------

        [Fact]
        public void Writing_a_new_pair_announces_the_change()
        {
            var table = Bound();
            int changes = 0;
            table.Changed += (s, e) => changes++;

            table[ContactLid] = ContactPn;

            Assert.Equal(1, changes);
        }

        [Fact]
        public void Restating_the_same_pair_is_silent()
        {
            // Live traffic repeats the same pair on every message; caches keyed by
            // canonical address must not be invalidated for it.
            var table = Bound();
            table[ContactLid] = ContactPn;

            int changes = 0;
            table.Changed += (s, e) => changes++;
            table[ContactLid] = ContactPn;

            Assert.Equal(0, changes);
        }

        [Fact]
        public void Removing_and_clearing_announce_the_change()
        {
            var table = Bound();
            table[ContactLid] = ContactPn;

            int changes = 0;
            table.Changed += (s, e) => changes++;

            Assert.True(table.Remove(ContactLid));
            Assert.Equal(1, changes);

            table[ContactLid] = ContactPn;
            table.Clear();
            Assert.Equal(3, changes);
        }

        [Fact]
        public void Removing_something_absent_is_silent()
        {
            var table = Bound();
            int changes = 0;
            table.Changed += (s, e) => changes++;

            Assert.False(table.Remove(ContactLid));
            table.Clear();

            Assert.Equal(0, changes);
        }

        // --- Snapshot --------------------------------------------------------

        [Fact]
        public void Snapshot_is_a_detached_copy()
        {
            var table = Bound();
            table[ContactLid] = ContactPn;

            var snapshot = table.Snapshot();
            table[ContactLid] = "someone.else@s.whatsapp.net";

            Assert.Equal(ContactPn, snapshot[ContactLid]);
        }
    }
}
