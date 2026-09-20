// =============================================================================
// Tests for MessageStatusProgression.
//
// These decide the ticks on every message the user sends, and until now the
// rule had no tests at all despite being consulted from four places.
//
// The scenarios below are written as the sequences that actually arrive from
// the server, because the rule only makes sense in that light: receipts are not
// ordered, so the question is never "what is the latest status" but "may this
// one replace what is already shown".
// =============================================================================
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class MessageStatusProgressionTests
    {
        // --- Moving forward ------------------------------------------------------

        [Theory]
        [InlineData(ChatMessage.StatusPending, ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusSent, ChatMessage.StatusDelivered)]
        [InlineData(ChatMessage.StatusDelivered, ChatMessage.StatusRead)]
        [InlineData(ChatMessage.StatusPending, ChatMessage.StatusRead)]
        public void Progress_is_accepted(string current, string incoming)
        {
            Assert.True(MessageStatusProgression.ShouldApply(current, incoming));
        }

        // --- Never going backwards -------------------------------------------------

        [Theory]
        [InlineData(ChatMessage.StatusRead, ChatMessage.StatusDelivered)]
        [InlineData(ChatMessage.StatusRead, ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusDelivered, ChatMessage.StatusSent)]
        [InlineData(ChatMessage.StatusSent, ChatMessage.StatusPending)]
        public void A_receipt_that_arrives_late_cannot_walk_the_ticks_back(string current, string incoming)
        {
            // Receipts are not ordered. A delivery receipt can land after the read receipt
            // it preceded, and taking the latest one would flicker the ticks backwards in
            // front of the user.
            Assert.False(MessageStatusProgression.ShouldApply(current, incoming));
        }

        [Fact]
        public void Repeating_the_status_already_shown_changes_nothing()
        {
            Assert.False(MessageStatusProgression.ShouldApply(ChatMessage.StatusRead, ChatMessage.StatusRead));
        }

        [Fact]
        public void The_same_status_in_different_casing_is_still_the_same_status()
        {
            Assert.False(MessageStatusProgression.ShouldApply(ChatMessage.StatusRead, "READ"));
        }

        // --- Failure ----------------------------------------------------------------

        [Theory]
        [InlineData(ChatMessage.StatusPending)]
        [InlineData(ChatMessage.StatusSent)]
        public void Failure_is_believed_while_the_message_might_still_be_in_flight(string current)
        {
            Assert.True(MessageStatusProgression.ShouldApply(current, ChatMessage.StatusFailed));
        }

        [Theory]
        [InlineData(ChatMessage.StatusDelivered)]
        [InlineData(ChatMessage.StatusRead)]
        public void Failure_cannot_undo_proof_that_the_message_arrived(string current)
        {
            // The important asymmetry. Once the recipient has it, a straggling error says
            // nothing about whether it was received -- and showing a failure mark on a
            // message that was demonstrably read would be worse than ignoring the error.
            Assert.False(MessageStatusProgression.ShouldApply(current, ChatMessage.StatusFailed));
        }

        [Fact]
        public void A_failed_message_can_still_recover_when_the_retry_succeeds()
        {
            // Failure sits below the ladder rather than on it, so any real progress
            // outranks it.
            Assert.True(MessageStatusProgression.ShouldApply(ChatMessage.StatusFailed, ChatMessage.StatusSent));
            Assert.True(MessageStatusProgression.ShouldApply(ChatMessage.StatusFailed, ChatMessage.StatusPending));
        }

        // --- Nothing to apply ----------------------------------------------------------

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void An_empty_report_is_not_a_status(string? incoming)
        {
            // Silence is not a demotion to pending.
            Assert.False(MessageStatusProgression.ShouldApply(ChatMessage.StatusRead, incoming));
        }

        [Fact]
        public void A_message_with_no_status_yet_accepts_the_first_real_one()
        {
            Assert.True(MessageStatusProgression.ShouldApply(null, ChatMessage.StatusSent));
        }

        // --- Ranking ---------------------------------------------------------------------

        [Fact]
        public void The_ladder_climbs_in_the_order_the_user_sees()
        {
            Assert.True(MessageStatusProgression.Rank(ChatMessage.StatusPending) < MessageStatusProgression.Rank(ChatMessage.StatusSent));
            Assert.True(MessageStatusProgression.Rank(ChatMessage.StatusSent) < MessageStatusProgression.Rank(ChatMessage.StatusDelivered));
            Assert.True(MessageStatusProgression.Rank(ChatMessage.StatusDelivered) < MessageStatusProgression.Rank(ChatMessage.StatusRead));
        }

        [Fact]
        public void Failure_ranks_below_every_real_state()
        {
            Assert.True(MessageStatusProgression.Rank(ChatMessage.StatusFailed) < MessageStatusProgression.Rank(ChatMessage.StatusPending));
        }

        [Fact]
        public void An_unrecognised_status_is_treated_as_the_weakest_real_state()
        {
            // Recorded: an unknown string ranks as pending, so it displaces nothing and can
            // be replaced by anything. A server adding a status we do not know about
            // degrades quietly instead of pinning the ticks.
            Assert.Equal(
                MessageStatusProgression.Rank(ChatMessage.StatusPending),
                MessageStatusProgression.Rank("some-future-status"));

            Assert.False(MessageStatusProgression.ShouldApply(ChatMessage.StatusRead, "some-future-status"));
        }
    }
}
