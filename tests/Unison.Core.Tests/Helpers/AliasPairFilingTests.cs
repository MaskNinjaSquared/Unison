// =============================================================================
// Tests for AliasPairFiling.
//
// How an accepted LID/phone pair is written into the alias table. Acceptance is
// AliasPairPolicy's job; these pin what happens after, which used to be spelled
// out by hand at three sites in WhatsAppService:
//
//   - the live alias path (TryRecordAliasMapping) and the usync answer
//     (TryFileUsyncAlias) write both directions, always;
//   - the duplicate-row merge (DeduplicateChatsAsync) writes the LID side, but
//     leaves the phone side alone when it already points at an @lid.
//
// The two shapes are kept apart on purpose. A usync answer or a live envelope is
// the server naming the pair; two rows found to be one person is our inference,
// and it must not repoint a phone address the server already tied to a LID.
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class AliasPairFilingTests
    {
        private const string ContactPn = "5511888888888@s.whatsapp.net";
        private const string ContactLid = "400500600@lid";
        private const string OtherLid = "700800900@lid";

        private static JidAliasTable Table() => new JidAliasTable();

        // --- FileBothWays -----------------------------------------------------

        [Fact]
        public void A_new_pair_is_filed_in_both_directions_and_reported_as_news()
        {
            var table = Table();

            bool changed = AliasPairFiling.FileBothWays(table, ContactLid, ContactPn);

            Assert.True(changed);
            Assert.Equal(ContactPn, table[ContactLid]);
            Assert.Equal(ContactLid, table[ContactPn]);
        }

        [Fact]
        public void Restating_a_known_pair_is_not_news()
        {
            // Live traffic re-states the same pair on every message; the caller skips the
            // follow-up work (avatar refetch, alias event) on exactly this answer.
            var table = Table();
            table[ContactLid] = ContactPn;
            table[ContactPn] = ContactLid;

            Assert.False(AliasPairFiling.FileBothWays(table, ContactLid, ContactPn));
        }

        [Fact]
        public void A_known_pair_stored_in_another_case_or_with_a_device_suffix_is_not_news()
        {
            var table = Table();
            table[ContactLid] = "5511888888888:12@S.WhatsApp.Net";

            Assert.False(AliasPairFiling.FileBothWays(table, ContactLid, ContactPn));
            Assert.Equal(ContactPn, table[ContactLid]);
        }

        [Fact]
        public void A_LID_that_moves_to_another_number_is_news()
        {
            var table = Table();
            table[ContactLid] = "5511777776666@s.whatsapp.net";

            Assert.True(AliasPairFiling.FileBothWays(table, ContactLid, ContactPn));
            Assert.Equal(ContactPn, table[ContactLid]);
        }

        [Fact]
        public void News_is_judged_from_the_LID_side_only()
        {
            // Recorded, not endorsed: a phone address that pointed at another LID is
            // overwritten, but the answer only looks at what the LID used to say.
            var table = Table();
            table[ContactLid] = ContactPn;
            table[ContactPn] = OtherLid;

            Assert.False(AliasPairFiling.FileBothWays(table, ContactLid, ContactPn));
            Assert.Equal(ContactLid, table[ContactPn]);
        }

        [Theory]
        [InlineData(null, ContactPn)]
        [InlineData("", ContactPn)]
        [InlineData(ContactLid, null)]
        [InlineData(ContactLid, "")]
        public void Half_a_pair_writes_nothing(string? lid, string? pn)
        {
            var table = Table();

            Assert.False(AliasPairFiling.FileBothWays(table, lid!, pn!));
            Assert.Empty(table);
        }

        [Fact]
        public void Without_a_table_nothing_is_filed()
        {
            Assert.False(AliasPairFiling.FileBothWays(null!, ContactLid, ContactPn));
        }

        // --- FileFromRowMerge -------------------------------------------------

        [Fact]
        public void A_row_merge_files_both_directions_when_the_phone_side_is_free()
        {
            var table = Table();

            AliasPairFiling.FileFromRowMerge(table, ContactLid, ContactPn);

            Assert.Equal(ContactPn, table[ContactLid]);
            Assert.Equal(ContactLid, table[ContactPn]);
        }

        [Fact]
        public void A_row_merge_does_not_repoint_a_phone_address_already_tied_to_a_LID()
        {
            // The server tied this number to a LID; two rows looking like one person is
            // our inference and does not get to overrule it.
            var table = Table();
            table[ContactPn] = OtherLid;

            AliasPairFiling.FileFromRowMerge(table, ContactLid, ContactPn);

            Assert.Equal(ContactPn, table[ContactLid]);
            Assert.Equal(OtherLid, table[ContactPn]);
        }

        [Fact]
        public void An_existing_LID_is_recognised_through_case_and_device_suffix()
        {
            var table = Table();
            table[ContactPn] = "700800900:3@LID";

            AliasPairFiling.FileFromRowMerge(table, ContactLid, ContactPn);

            Assert.Equal("700800900:3@LID", table[ContactPn]);
        }

        [Fact]
        public void A_phone_side_pointing_at_something_other_than_a_LID_is_overwritten()
        {
            // Only an @lid is "more specific" than what the merge knows. A dotted LID on
            // the phone domain does not count -- recorded, not endorsed.
            var table = Table();
            table[ContactPn] = "931777777.11@s.whatsapp.net";

            AliasPairFiling.FileFromRowMerge(table, ContactLid, ContactPn);

            Assert.Equal(ContactLid, table[ContactPn]);
        }

        [Fact]
        public void A_blank_reverse_entry_is_treated_as_free()
        {
            var table = Table();
            table[ContactPn] = "  ";

            AliasPairFiling.FileFromRowMerge(table, ContactLid, ContactPn);

            Assert.Equal(ContactLid, table[ContactPn]);
        }

        [Theory]
        [InlineData(null, ContactPn)]
        [InlineData("", ContactPn)]
        [InlineData(ContactLid, null)]
        [InlineData(ContactLid, "")]
        public void A_row_merge_with_half_a_pair_writes_nothing(string? lid, string? pn)
        {
            var table = Table();

            AliasPairFiling.FileFromRowMerge(table, lid!, pn!);

            Assert.Empty(table);
        }
    }
}
