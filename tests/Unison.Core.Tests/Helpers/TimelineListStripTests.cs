// =============================================================================
// Tests for TimelineListStrip.
//
// Opening a chat is the moment its list row can be corrected from what the
// timeline actually holds. Wrong here leaves a stale "last message" in the list.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class TimelineListStripTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc);

        private static ChatMessage Msg(string id, int seconds, bool fromMe = false) =>
            new ChatMessage { Id = id, Timestamp = T0.AddSeconds(seconds), IsFromMe = fromMe };

        private static ChatItem Row(string id, int? seconds, string preview = "hi", bool fromMe = false) =>
            new ChatItem
            {
                JID = "5511999999999@s.whatsapp.net",
                LastMessageId = id,
                LastMessage = preview,
                LastMessageKind = ChatPreviewKind.Text,
                LastMessageAuthor = string.Empty,
                LastMessageIsFromMe = fromMe,
                LastMessageSendState = MessageSendState.NotApplicable,
                LastMessageTimestampUtc = seconds.HasValue ? T0.AddSeconds(seconds.Value) : (DateTime?)null
            };

        // --- PickTip ---------------------------------------------------------

        [Fact]
        public void The_tip_is_the_newest_message_not_the_last_bubble()
        {
            var timeline = new[] { Msg("a", 0), Msg("c", 30), Msg("b", 10) };

            Assert.Equal("c", TimelineListStrip.PickTip(timeline)?.Id);
        }

        [Fact]
        public void On_the_same_clock_the_higher_id_wins()
        {
            var timeline = new[] { Msg("bbb", 5), Msg("aaa", 5) };

            Assert.Equal("bbb", TimelineListStrip.PickTip(timeline)?.Id);
        }

        [Fact]
        public void Messages_without_a_clock_and_gaps_are_skipped()
        {
            var noClock = new ChatMessage { Id = "zzz", Timestamp = DateTime.MinValue };
            var timeline = new[] { null, noClock, Msg("a", 1) };

            Assert.Equal("a", TimelineListStrip.PickTip(timeline)?.Id);
            Assert.Null(TimelineListStrip.PickTip(new[] { noClock }));
            Assert.Null(TimelineListStrip.PickTip(null));
        }

        // --- ShouldReplace ---------------------------------------------------

        [Fact]
        public void A_different_tip_id_replaces_even_an_older_looking_strip()
        {
            // The strip clock may be poisoned (+3h); the id is the stronger signal.
            ChatItem row = Row("old", seconds: 100);

            Assert.True(TimelineListStrip.ShouldReplace(
                row, Msg("new", 10), "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
        }

        [Fact]
        public void A_newer_tip_replaces_the_strip()
        {
            ChatItem row = Row(null, seconds: 10);
            ChatMessage tip = new ChatMessage { Id = null, Timestamp = T0.AddSeconds(20) };

            Assert.True(TimelineListStrip.ShouldReplace(
                row, tip, "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
        }

        [Fact]
        public void The_same_tip_rendered_the_same_way_is_left_alone()
        {
            ChatItem row = Row("a", seconds: 10);

            Assert.False(TimelineListStrip.ShouldReplace(
                row, Msg("a", 10), "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
        }

        [Fact]
        public void The_same_tip_rendered_differently_refreshes_the_strip()
        {
            ChatItem row = Row("a", seconds: 10, preview: "old text");

            Assert.True(TimelineListStrip.ShouldReplace(
                row, Msg("a", 10), "new text", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
            Assert.True(TimelineListStrip.ShouldReplace(
                Row("a", 10), Msg("a", 10), "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.Read));
        }

        [Fact]
        public void An_older_tip_never_replaces_a_newer_strip_with_the_same_id()
        {
            ChatItem row = Row("a", seconds: 30, preview: "other");

            Assert.False(TimelineListStrip.ShouldReplace(
                row, Msg("a", 10), "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
        }

        [Fact]
        public void A_strip_with_no_clock_takes_any_dated_tip()
        {
            ChatItem row = Row(null, seconds: null);
            ChatMessage tip = new ChatMessage { Id = null, Timestamp = T0 };

            Assert.True(TimelineListStrip.ShouldReplace(
                row, tip, "hi", ChatPreviewKind.Text, string.Empty, MessageSendState.NotApplicable));
        }
    }
}
