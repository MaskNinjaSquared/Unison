// =============================================================================
// Tests for ChatPreviewStaleness.
//
// The gate every preview write passes through. Previews arrive from live
// messages, history catch-up, offline replay and SQLite reconciliation, and
// they do not arrive in order -- so "newer" has to be decided rather than
// assumed from arrival.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewStalenessTests
    {
        private static readonly DateTime Noon = new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        private static bool Accepts(DateTime? current, DateTime candidate, bool force = false) =>
            ChatPreviewStaleness.ShouldAccept(current, candidate, force);

        // --- The ordinary case -------------------------------------------------

        [Fact]
        public void A_newer_message_replaces_what_the_row_is_showing()
        {
            Assert.True(Accepts(Noon, Noon.AddMinutes(1)));
        }

        [Fact]
        public void An_older_message_does_not()
        {
            // A history chunk delivered after a live message would otherwise walk the
            // conversation backwards in the list.
            Assert.False(Accepts(Noon, Noon.AddMinutes(-1)));
        }

        [Fact]
        public void The_same_message_arriving_again_is_let_through()
        {
            // It often carries more the second time: a delivery receipt, a caption that
            // finished downloading, an author that has since been resolved.
            Assert.True(Accepts(Noon, Noon));
        }

        // --- A row with nothing on it -------------------------------------------

        [Fact]
        public void A_row_showing_nothing_yet_accepts_anything_dated()
        {
            Assert.True(Accepts(null, Noon));
        }

        [Fact]
        public void A_row_whose_stored_instant_is_meaningless_accepts_anything_dated()
        {
            // Rows written by older versions can carry MinValue rather than null.
            Assert.True(Accepts(DateTime.MinValue, Noon));
        }

        [Fact]
        public void An_old_message_still_beats_a_row_showing_nothing()
        {
            Assert.True(Accepts(null, new DateTime(2019, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        }

        // --- Messages with no usable instant -------------------------------------

        [Fact]
        public void A_message_with_no_instant_never_relabels_a_row_on_its_own()
        {
            // It stays in the conversation; it just cannot claim to be the latest.
            Assert.False(Accepts(Noon, DateTime.MinValue));
        }

        [Fact]
        public void That_holds_even_when_the_row_is_showing_nothing()
        {
            // Accepting it would also move the chat to the top of the list on no evidence.
            Assert.False(Accepts(null, DateTime.MinValue));
        }

        // --- force ----------------------------------------------------------------

        [Fact]
        public void A_forced_write_overrides_a_newer_row()
        {
            // The reconcile pass compares every source itself, then forces. A row whose
            // stored instant is wrong must not be able to refuse the correction meant to
            // fix it -- which is the situation force exists for.
            Assert.True(Accepts(Noon.AddHours(3), Noon, force: true));
        }

        [Fact]
        public void A_forced_write_is_accepted_with_no_instant_at_all()
        {
            Assert.True(Accepts(Noon, DateTime.MinValue, force: true));
        }

        [Fact]
        public void Without_force_that_same_write_is_refused()
        {
            // Pins the pair: the only difference between these two cases is the flag.
            Assert.False(Accepts(Noon.AddHours(3), Noon, force: false));
        }

        // --- Kind handling ----------------------------------------------------------

        [Fact]
        public void A_candidate_read_back_from_the_store_is_not_treated_as_local_time()
        {
            // Unspecified is already UTC wall-clock here. Read as local, this candidate
            // would appear three hours newer in Brazil and wrongly win.
            DateTime fromStore = DateTime.SpecifyKind(Noon, DateTimeKind.Unspecified);

            Assert.False(Accepts(Noon.AddMinutes(1), fromStore));
        }

        [Fact]
        public void The_rows_own_instant_gets_the_same_treatment()
        {
            DateTime storedOnRow = DateTime.SpecifyKind(Noon, DateTimeKind.Unspecified);

            Assert.True(Accepts(storedOnRow, Noon.AddMinutes(1)));
            Assert.False(Accepts(storedOnRow, Noon.AddMinutes(-1)));
        }

        [Fact]
        public void Two_rows_of_different_kinds_at_the_same_instant_are_equal()
        {
            DateTime fromStore = DateTime.SpecifyKind(Noon, DateTimeKind.Unspecified);

            Assert.True(Accepts(fromStore, Noon));
            Assert.True(Accepts(Noon, fromStore));
        }
    }
}
