// =============================================================================
// Tests for ChatListDisplayOrder pin/mute carry across PN/LID aliases.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListDisplayOrderPinMuteTests
    {
        [Fact]
        public void Newer_unpinned_alias_keeps_the_pin_from_the_older_row()
        {
            // ApplyTo used to stamp PinnedTimestamp=0 on an unpinned stub; the old carry rule
            // treated that as an explicit unpin and dropped the favourite on dedupe.
            var pinned = new ChatItem
            {
                JID = "5511@lid",
                IsChatPinned = true,
                PinnedTimestamp = 50,
                LastMessageTimestampUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            var newer = new ChatItem
            {
                JID = "5511@s.whatsapp.net",
                IsChatPinned = false,
                PinnedTimestamp = 0,
                LastMessageTimestampUtc = DateTime.UtcNow
            };

            List<ChatItem> result = ChatListDisplayOrder.DeduplicateByCanonicalJid(
                new[] { pinned, newer },
                jid => "5511@s.whatsapp.net");

            Assert.Single(result);
            Assert.True(result[0].IsChatPinned);
            Assert.Equal(50, result[0].PinnedTimestamp);
        }

        [Fact]
        public void Mute_is_carried_onto_the_surviving_alias()
        {
            var muted = new ChatItem
            {
                JID = "5511@lid",
                MutedUntil = 99,
                LastMessageTimestampUtc = DateTime.UtcNow.AddMinutes(-10)
            };
            var newer = new ChatItem
            {
                JID = "5511@s.whatsapp.net",
                MutedUntil = null,
                LastMessageTimestampUtc = DateTime.UtcNow
            };

            List<ChatItem> result = ChatListDisplayOrder.DeduplicateByCanonicalJid(
                new[] { muted, newer },
                jid => "5511@s.whatsapp.net");

            Assert.Single(result);
            Assert.Equal(99, result[0].MutedUntil);
        }
    }
}
