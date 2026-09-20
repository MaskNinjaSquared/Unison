// =============================================================================
// Tests for GroupReceiptTally.
//
// The check marks on a group message. Previously only observable by sending to
// a real group from a real phone and watching.
// =============================================================================
using System;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.State
{
    public class GroupReceiptTallyTests
    {
        private static readonly DateTime Now = new DateTime(2026, 3, 1, 9, 0, 0, DateTimeKind.Utc);

        private static GroupReceiptTally Tally() => new GroupReceiptTally();

        private static string Delivered(GroupReceiptTally tally, string participant, int expected = 2, string messageId = "m1") =>
            tally.Register(messageId, participant, ChatMessage.StatusDelivered, expected, Now);

        private static string Read(GroupReceiptTally tally, string participant, int expected = 2, string messageId = "m1") =>
            tally.Register(messageId, participant, ChatMessage.StatusRead, expected, Now);

        [Fact]
        public void One_recipient_out_of_two_concludes_nothing()
        {
            var tally = Tally();

            Assert.Null(Delivered(tally, "a@s.whatsapp.net"));
        }

        [Fact]
        public void Every_recipient_delivering_turns_the_message_delivered()
        {
            var tally = Tally();

            Assert.Null(Delivered(tally, "a@s.whatsapp.net"));
            Assert.Equal(ChatMessage.StatusDelivered, Delivered(tally, "b@s.whatsapp.net"));
        }

        [Fact]
        public void Every_recipient_reading_turns_the_message_read()
        {
            var tally = Tally();

            Assert.Null(Read(tally, "a@s.whatsapp.net"));
            Assert.Equal(ChatMessage.StatusRead, Read(tally, "b@s.whatsapp.net"));
        }

        [Fact]
        public void Reading_counts_as_receiving_too()
        {
            // The delivered receipt may never arrive on its own, so a group where one
            // reads and the other only receives must still reach delivered.
            var tally = Tally();

            Assert.Null(Read(tally, "a@s.whatsapp.net"));
            Assert.Equal(ChatMessage.StatusDelivered, Delivered(tally, "b@s.whatsapp.net"));
        }

        [Fact]
        public void The_same_participant_reporting_twice_does_not_count_twice()
        {
            // Otherwise one chatty device would show two check marks for everyone.
            var tally = Tally();

            Assert.Null(Delivered(tally, "a@s.whatsapp.net"));
            Assert.Null(Delivered(tally, "a@s.whatsapp.net"));
        }

        [Fact]
        public void The_same_participant_in_a_different_case_is_the_same_participant()
        {
            var tally = Tally();

            Assert.Null(Delivered(tally, "a@s.whatsapp.net"));
            Assert.Null(Delivered(tally, "A@S.WhatsApp.net"));
        }

        [Fact]
        public void Delivered_can_still_become_read_afterwards()
        {
            var tally = Tally();

            Delivered(tally, "a@s.whatsapp.net");
            Assert.Equal(ChatMessage.StatusDelivered, Delivered(tally, "b@s.whatsapp.net"));

            Read(tally, "a@s.whatsapp.net");
            Assert.Equal(ChatMessage.StatusRead, Read(tally, "b@s.whatsapp.net"));
        }

        [Fact]
        public void A_message_everyone_read_stops_being_tracked()
        {
            // Read is terminal, so keeping the tally would only leak memory.
            var tally = Tally();

            Read(tally, "a@s.whatsapp.net");
            Read(tally, "b@s.whatsapp.net");

            Assert.Equal(0, tally.TrackedMessageCount);
        }

        [Fact]
        public void A_message_still_waiting_stays_tracked()
        {
            var tally = Tally();

            Delivered(tally, "a@s.whatsapp.net");

            Assert.Equal(1, tally.TrackedMessageCount);
        }

        [Fact]
        public void Messages_are_tallied_independently()
        {
            var tally = Tally();

            Delivered(tally, "a@s.whatsapp.net", messageId: "m1");
            Assert.Null(Delivered(tally, "a@s.whatsapp.net", messageId: "m2"));
        }

        [Fact]
        public void A_group_of_one_is_concluded_by_a_single_receipt()
        {
            var tally = Tally();

            Assert.Equal(ChatMessage.StatusDelivered, Delivered(tally, "a@s.whatsapp.net", expected: 1));
        }

        [Fact]
        public void More_receipts_than_expected_still_conclude()
        {
            // Members can join between sending and reporting, so the threshold is a floor.
            var tally = Tally();

            Read(tally, "a@s.whatsapp.net", expected: 2);
            Read(tally, "b@s.whatsapp.net", expected: 2);
            Assert.Equal(ChatMessage.StatusRead, Read(tally, "c@s.whatsapp.net", expected: 1));
        }

        [Fact]
        public void A_status_that_is_neither_delivered_nor_read_counts_for_nobody()
        {
            var tally = Tally();

            Assert.Null(tally.Register("m1", "a@s.whatsapp.net", ChatMessage.StatusSent, 1, Now));
        }

        [Fact]
        public void Receipts_missing_what_identifies_them_are_ignored()
        {
            var tally = Tally();

            Assert.Null(tally.Register(null, "a@s.whatsapp.net", ChatMessage.StatusRead, 1, Now));
            Assert.Null(tally.Register("m1", null, ChatMessage.StatusRead, 1, Now));
            Assert.Null(tally.Register("m1", "   ", ChatMessage.StatusRead, 1, Now));
            Assert.Equal(0, tally.TrackedMessageCount);
        }

        [Fact]
        public void An_unknown_recipient_count_concludes_nothing_and_tracks_nothing()
        {
            // The caller could not read the group's member list.
            var tally = Tally();

            Assert.Null(tally.Register("m1", "a@s.whatsapp.net", ChatMessage.StatusRead, 0, Now));
            Assert.Equal(0, tally.TrackedMessageCount);
        }

        [Fact]
        public void A_heavy_group_sender_does_not_grow_the_tally_without_bound()
        {
            var tally = Tally();

            for (int i = 0; i < 600; i++)
            {
                tally.Register("old-" + i, "a@s.whatsapp.net", ChatMessage.StatusDelivered, 99, Now);
            }

            // One more receipt a week later finds the earlier ones stale and sweeps a batch.
            tally.Register("fresh", "a@s.whatsapp.net", ChatMessage.StatusDelivered, 99, Now.AddDays(7));

            Assert.True(tally.TrackedMessageCount < 600);
        }

        [Fact]
        public void Messages_still_being_reported_on_are_not_swept_away()
        {
            // A busy but healthy account must not lose the check marks it is waiting for.
            var tally = Tally();

            for (int i = 0; i < 600; i++)
            {
                tally.Register("m-" + i, "a@s.whatsapp.net", ChatMessage.StatusDelivered, 99, Now);
            }

            tally.Register("m-fresh", "a@s.whatsapp.net", ChatMessage.StatusDelivered, 99, Now);

            Assert.Equal(601, tally.TrackedMessageCount);
        }
    }
}
