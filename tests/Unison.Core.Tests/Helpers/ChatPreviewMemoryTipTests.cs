// =============================================================================
// ChatPreviewMemoryTip — frugal path must not Snapshot the whole message cache.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;
using Unison.Core.State;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatPreviewMemoryTipTests
    {
        private static readonly DateTime Now =
            new DateTime(2026, 10, 28, 5, 28, 0, DateTimeKind.Utc);

        [Fact]
        public void Keys_hit_returns_tip_without_needing_full_scan()
        {
            var cache = new ChatMessageCache();
            var newer = MakeMessage("m-new", Now.AddMinutes(-1));
            var older = MakeMessage("m-old", Now.AddMinutes(-10));
            cache.Set("555@s.whatsapp.net", new List<ChatMessage> { older, newer });

            ChatMessage tip = ChatPreviewMemoryTip.FindNewest(
                cache,
                "555@s.whatsapp.net",
                new[] { "555@s.whatsapp.net" },
                jid => jid,
                allowFullCacheScan: false,
                Now);

            Assert.NotNull(tip);
            Assert.Equal("m-new", tip.Id);
        }

        [Fact]
        public void Frugal_skips_orphan_alias_that_only_full_scan_would_find()
        {
            var cache = new ChatMessageCache();
            var tip = MakeMessage("orphan", Now.AddMinutes(-2));
            // Stored under an alias key the expander did not pass.
            cache.Set("555@lid", new List<ChatMessage> { tip });

            ChatMessage found = ChatPreviewMemoryTip.FindNewest(
                cache,
                "555@s.whatsapp.net",
                new[] { "555@s.whatsapp.net" },
                jid => jid.EndsWith("@lid", StringComparison.Ordinal)
                    ? "555@s.whatsapp.net"
                    : jid,
                allowFullCacheScan: false,
                Now);

            Assert.Null(found);
        }

        [Fact]
        public void Generous_full_scan_finds_orphan_alias_tip()
        {
            var cache = new ChatMessageCache();
            var tip = MakeMessage("orphan", Now.AddMinutes(-2));
            cache.Set("555@lid", new List<ChatMessage> { tip });

            ChatMessage found = ChatPreviewMemoryTip.FindNewest(
                cache,
                "555@s.whatsapp.net",
                new[] { "555@s.whatsapp.net" },
                jid => jid.EndsWith("@lid", StringComparison.Ordinal)
                    ? "555@s.whatsapp.net"
                    : jid,
                allowFullCacheScan: true,
                Now);

            Assert.NotNull(found);
            Assert.Equal("orphan", found.Id);
        }

        private static ChatMessage MakeMessage(string id, DateTime ts) =>
            new ChatMessage { Id = id, Timestamp = ts, Content = "hi" };
    }
}
