// =============================================================================
// Tests for NewestStoredMessage.
//
// History freshness asks "how new is the newest message we hold". On a cold start
// the timelines are mostly empty until a chat opens, so the list strip's tip times
// (read from SQLite) count too, or freshness reads "no stored messages" forever.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NewestStoredMessageTests
    {
        private const string Alice = "5511999990001@s.whatsapp.net";
        private const string Group = "120363000000000000@g.us";
        private static readonly DateTime T0 = new DateTime(2026, 3, 10, 12, 0, 0, DateTimeKind.Utc);

        private readonly List<KeyValuePair<string, List<ChatMessage>>> _timelines =
            new List<KeyValuePair<string, List<ChatMessage>>>();
        private readonly List<ChatItem> _chats = new List<ChatItem>();

        private void Timeline(string jid, params DateTime[] timestamps)
        {
            var messages = new List<ChatMessage>();
            foreach (DateTime timestamp in timestamps)
            {
                messages.Add(new ChatMessage { Timestamp = timestamp });
            }

            _timelines.Add(new KeyValuePair<string, List<ChatMessage>>(jid, messages));
        }

        private void Strip(string jid, DateTime? tipUtc) =>
            _chats.Add(new ChatItem { JID = jid, LastMessageTimestampUtc = tipUtc });

        private DateTime Newest(Func<string, bool> includeChat = null) =>
            NewestStoredMessage.FindUtc(_timelines, _chats, includeChat);

        [Fact]
        public void Nothing_held_is_min_value()
        {
            Assert.Equal(DateTime.MinValue, Newest());
            Assert.Equal(DateTime.MinValue, NewestStoredMessage.FindUtc(null, null, null));
        }

        [Fact]
        public void The_newest_message_across_timelines_wins()
        {
            Timeline(Alice, T0, T0.AddMinutes(5));
            Timeline(Group, T0.AddMinutes(2));

            Assert.Equal(T0.AddMinutes(5), Newest());
        }

        [Fact]
        public void A_strip_tip_newer_than_any_timeline_wins()
        {
            Timeline(Alice, T0);
            Strip(Group, T0.AddHours(1));

            Assert.Equal(T0.AddHours(1), Newest());
        }

        [Fact]
        public void A_cold_start_reads_the_strip_alone()
        {
            Strip(Alice, T0);
            Strip(Group, null);

            Assert.Equal(T0, Newest());
        }

        [Fact]
        public void The_filter_applies_to_timelines_and_strip_alike()
        {
            Timeline(Alice, T0.AddHours(2));
            Strip(Alice, T0.AddHours(3));
            Timeline(Group, T0);
            Strip(Group, T0.AddMinutes(1));

            Assert.Equal(T0.AddMinutes(1), Newest(jid => jid.EndsWith("@g.us", StringComparison.Ordinal)));
        }

        [Fact]
        public void The_filter_sees_normalized_jids()
        {
            Timeline("5511999990001:3@s.whatsapp.net", T0);
            var seen = new List<string>();

            Newest(jid => { seen.Add(jid); return true; });

            Assert.Equal(new[] { JidHelper.Normalize("5511999990001:3@s.whatsapp.net") }, seen);
        }

        [Fact]
        public void Unspecified_rows_read_back_from_sqlite_are_compared_as_utc()
        {
            DateTime unspecified = DateTime.SpecifyKind(T0.AddMinutes(1), DateTimeKind.Unspecified);
            Timeline(Alice, T0, unspecified);

            Assert.Equal(ChatMessageOrder.ToComparableUtc(unspecified), Newest());
        }

        [Fact]
        public void Missing_entries_are_skipped()
        {
            _timelines.Add(new KeyValuePair<string, List<ChatMessage>>(Alice, null));
            _timelines.Add(new KeyValuePair<string, List<ChatMessage>>(Group, new List<ChatMessage> { null }));
            _chats.Add(null);
            Strip(Alice, T0);

            Assert.Equal(T0, Newest());
        }
    }
}
