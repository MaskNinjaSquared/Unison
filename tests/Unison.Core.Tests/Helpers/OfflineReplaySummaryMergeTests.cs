// =============================================================================
// Tests for OfflineReplaySummaryMerge.
//
// Offline replay keeps messages off the UI thread and folds a compact tip per
// chat. Record and rollback used to be two hand-written copies that could
// leave AuthorPrefix behind on reapply.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class OfflineReplaySummaryMergeTests
    {
        private static readonly DateTime Noon =
            new DateTime(2026, 5, 20, 12, 0, 0, DateTimeKind.Utc);

        private static OfflineReplayChatSummary Empty() =>
            OfflineReplaySummaryMerge.Create("120363000000000000@g.us", isGroup: true);

        [Fact]
        public void A_newer_tip_replaces_every_field_that_describes_it()
        {
            var summary = Empty();
            OfflineReplaySummaryMerge.Record(
                summary,
                "first",
                Noon,
                isGroup: true,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: "delivered",
                authorPrefix: "Ana: ",
                incrementUnread: false,
                utcNow: Noon);

            OfflineReplaySummaryMerge.Record(
                summary,
                "second",
                Noon.AddMinutes(1),
                isGroup: true,
                isFromMe: true,
                ChatPreviewKind.Image,
                status: "read",
                authorPrefix: "You: ",
                incrementUnread: false,
                utcNow: Noon.AddMinutes(1));

            Assert.Equal("second", summary.Preview);
            Assert.Equal(ChatPreviewKind.Image, summary.Kind);
            Assert.Equal("read", summary.Status);
            Assert.Equal("You: ", summary.AuthorPrefix);
            Assert.True(summary.IsFromMe);
        }

        [Fact]
        public void An_older_tip_does_not_overwrite_but_unread_still_accumulates()
        {
            var summary = Empty();
            OfflineReplaySummaryMerge.Record(
                summary,
                "live",
                Noon,
                isGroup: true,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: "Ana: ",
                incrementUnread: true,
                utcNow: Noon);

            OfflineReplaySummaryMerge.Record(
                summary,
                "echo",
                Noon.AddMinutes(-5),
                isGroup: true,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: "Bob: ",
                incrementUnread: true,
                utcNow: Noon);

            Assert.Equal("live", summary.Preview);
            Assert.Equal("Ana: ", summary.AuthorPrefix);
            Assert.Equal(2, summary.UnreadDelta);
        }

        [Fact]
        public void An_equal_timestamp_on_record_still_refreshes_the_tip()
        {
            var summary = Empty();
            OfflineReplaySummaryMerge.Record(
                summary,
                "old body",
                Noon,
                isGroup: false,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: null,
                incrementUnread: false,
                utcNow: Noon);

            OfflineReplaySummaryMerge.Record(
                summary,
                "new body",
                Noon,
                isGroup: false,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: null,
                incrementUnread: false,
                utcNow: Noon);

            Assert.Equal("new body", summary.Preview);
        }

        [Fact]
        public void An_invalid_timestamp_never_becomes_the_tip()
        {
            var summary = Empty();
            OfflineReplaySummaryMerge.Record(
                summary,
                "nope",
                DateTime.MinValue,
                isGroup: false,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: null,
                incrementUnread: true,
                utcNow: Noon);

            Assert.Equal(DateTime.MinValue, summary.Timestamp);
            Assert.Equal(string.Empty, summary.Preview);
            Assert.Equal(1, summary.UnreadDelta);
        }

        [Fact]
        public void Reapply_copies_author_prefix_with_a_newer_tip()
        {
            // The rollback path used to move Status/Kind but leave AuthorPrefix behind.
            var current = Empty();
            OfflineReplaySummaryMerge.Record(
                current,
                "kept",
                Noon,
                isGroup: true,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: "sent",
                authorPrefix: "Old: ",
                incrementUnread: false,
                utcNow: Noon);

            var returned = OfflineReplaySummaryMerge.Create(current.Jid, true);
            OfflineReplaySummaryMerge.Record(
                returned,
                "restored",
                Noon.AddMinutes(2),
                isGroup: true,
                isFromMe: false,
                ChatPreviewKind.Voice,
                status: "delivered",
                authorPrefix: "Ana: ",
                incrementUnread: true,
                utcNow: Noon.AddMinutes(2));

            OfflineReplaySummaryMerge.Reapply(current, returned);

            Assert.Equal("restored", current.Preview);
            Assert.Equal(ChatPreviewKind.Voice, current.Kind);
            Assert.Equal("delivered", current.Status);
            Assert.Equal("Ana: ", current.AuthorPrefix);
            Assert.Equal(1, current.UnreadDelta);
        }

        [Fact]
        public void Reapply_adds_unread_even_when_the_returned_tip_is_older()
        {
            var current = Empty();
            OfflineReplaySummaryMerge.Record(
                current,
                "newer",
                Noon,
                isGroup: false,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: null,
                incrementUnread: false,
                utcNow: Noon);

            var returned = OfflineReplaySummaryMerge.Create(current.Jid, false);
            OfflineReplaySummaryMerge.Record(
                returned,
                "older",
                Noon.AddMinutes(-1),
                isGroup: false,
                isFromMe: false,
                ChatPreviewKind.Text,
                status: null,
                authorPrefix: null,
                incrementUnread: true,
                utcNow: Noon);

            OfflineReplaySummaryMerge.Reapply(current, returned);

            Assert.Equal("newer", current.Preview);
            Assert.Equal(1, current.UnreadDelta);
        }
    }
}
