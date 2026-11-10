using System;
using System.Collections.Generic;
using System.Linq;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TimelineExtrasSelectorTests
    {
        private static readonly DateTime Now = new DateTime(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);

        private static HistoryMessage Pin(string id, int minutesAgo, DateTime? expires = null) =>
            new HistoryMessage
            {
                MessageId = id,
                IsPinned = true,
                PinnedAtUtc = Now.AddMinutes(-minutesAgo),
                PinExpiresAtUtc = expires,
                TimestampUtc = Now.AddDays(-1),
            };

        private static HistoryMessage Outgoing(string id, MessageSendState state, int minutesAgo) =>
            new HistoryMessage
            {
                MessageId = id,
                IsFromMe = true,
                SendState = state,
                TimestampUtc = Now.AddMinutes(-minutesAgo),
            };

        [Fact]
        public void Pins_newest_first_capped_and_expired_skipped()
        {
            var rows = new List<HistoryMessage>
            {
                Pin("old", 30),
                Pin("expired", 1, Now.AddMinutes(-1)),
                Pin("new", 5),
                Pin("mid", 10),
                Pin("oldest", 60),
            };

            IReadOnlyList<HistoryMessage> picked = TimelineExtrasSelector.Select(rows, 3, Now);

            Assert.Equal(new[] { "new", "mid", "old" }, picked.Select(m => m.MessageId));
        }

        [Fact]
        public void Pending_and_failed_outgoing_follow_pins_oldest_first()
        {
            var rows = new List<HistoryMessage>
            {
                Outgoing("failed", MessageSendState.Failed, 1),
                Outgoing("sent", MessageSendState.Sent, 2),
                Pin("pin", 5),
                Outgoing("pending", MessageSendState.Pending, 9),
            };

            IReadOnlyList<HistoryMessage> picked = TimelineExtrasSelector.Select(rows, 3, Now);

            Assert.Equal(new[] { "pin", "pending", "failed" }, picked.Select(m => m.MessageId));
        }

        [Fact]
        public void Same_message_under_two_alias_keys_appears_once()
        {
            var rows = new List<HistoryMessage>
            {
                Pin("dup", 5),
                Pin("dup", 5),
                Outgoing("p", MessageSendState.Pending, 3),
                Outgoing("p", MessageSendState.Pending, 3),
            };

            IReadOnlyList<HistoryMessage> picked = TimelineExtrasSelector.Select(rows, 3, Now);

            Assert.Equal(new[] { "dup", "p" }, picked.Select(m => m.MessageId));
        }

        [Fact]
        public void Pinned_pending_message_is_listed_once_as_pin()
        {
            HistoryMessage both = Outgoing("both", MessageSendState.Pending, 2);
            both.IsPinned = true;
            both.PinnedAtUtc = Now;

            IReadOnlyList<HistoryMessage> picked =
                TimelineExtrasSelector.Select(new List<HistoryMessage> { both }, 3, Now);

            Assert.Single(picked);
        }

        [Fact]
        public void Null_or_blank_rows_are_ignored()
        {
            var rows = new List<HistoryMessage> { null, new HistoryMessage { MessageId = " ", IsPinned = true } };

            Assert.Empty(TimelineExtrasSelector.Select(rows, 3, Now));
            Assert.Empty(TimelineExtrasSelector.Select(null, 3, Now));
        }
    }
}
