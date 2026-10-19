// =============================================================================
// Tests for LegacyDeletedPreview: which rows an old build left showing a replayed
// revoke, and which stored message replaces that line.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LegacyDeletedPreviewTests
    {
        private static readonly DateTime Now = new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

        private static ChatMessage Message(string id, string content, DateTime at, bool revoked = false) =>
            new ChatMessage { Id = id, Content = content, Timestamp = at, IsRevoked = revoked };

        [Fact]
        public void A_row_showing_the_marker_is_suspect_whatever_the_case()
        {
            Assert.True(LegacyDeletedPreview.IsSuspect(
                new ChatItem { JID = "1@s.whatsapp.net", LastMessage = "[message deleted]" }));
        }

        [Fact]
        public void A_row_without_an_address_is_not_suspect()
        {
            Assert.False(LegacyDeletedPreview.IsSuspect(
                new ChatItem { JID = " ", LastMessage = "[Message Deleted]" }));
        }

        [Fact]
        public void An_ordinary_row_is_not_suspect()
        {
            Assert.False(LegacyDeletedPreview.IsSuspect(
                new ChatItem { JID = "1@s.whatsapp.net", LastMessage = "hi" }));
            Assert.False(LegacyDeletedPreview.IsSuspect(null));
        }

        [Fact]
        public void The_newest_real_message_replaces_the_line()
        {
            var rows = new List<ChatMessage>
            {
                Message("a", "older", Now.AddHours(-2)),
                Message("b", "newer", Now.AddHours(-1))
            };

            Assert.Equal("b", LegacyDeletedPreview.PickReplacement(rows, Now).Id);
        }

        [Fact]
        public void Markers_revokes_and_impossible_clocks_are_skipped()
        {
            var rows = new List<ChatMessage>
            {
                Message("good", "kept", Now.AddHours(-3)),
                Message("revoked", "gone", Now.AddHours(-2), revoked: true),
                Message("marker", "[Message Deleted]", Now.AddHours(-1)),
                Message("future", "later", Now.AddDays(30)),
                null
            };

            Assert.Equal("good", LegacyDeletedPreview.PickReplacement(rows, Now).Id);
        }

        [Fact]
        public void Nothing_usable_means_no_replacement()
        {
            Assert.Null(LegacyDeletedPreview.PickReplacement(
                new List<ChatMessage> { Message("x", "[Message Deleted]", Now.AddHours(-1)) },
                Now));
            Assert.Null(LegacyDeletedPreview.PickReplacement(null, Now));
        }
    }
}
