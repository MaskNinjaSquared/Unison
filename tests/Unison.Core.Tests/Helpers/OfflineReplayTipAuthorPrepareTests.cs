using System;
using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class OfflineReplayTipAuthorPrepareTests
    {
        [Fact]
        public void Enrich_resolves_author_and_fills_cache()
        {
            var cache = new SessionTipAuthorCache();
            var summary = new OfflineReplayChatSummary
            {
                IsGroup = true,
                ParticipantJid = "111@lid",
                Preview = "oi"
            };
            int resolves = 0;

            OfflineReplayTipAuthorPrepare.Enrich(
                summary,
                cache,
                raw => raw == "111@lid" ? "5511999@s.whatsapp.net" : raw,
                jid =>
                {
                    resolves++;
                    return jid.Contains("5511") ? "Maria" : null;
                });

            Assert.Equal("Maria", summary.SenderName);
            Assert.Equal("Maria: ", summary.AuthorPrefix);
            Assert.Equal(1, resolves);
            Assert.True(cache.TryGet("5511999@s.whatsapp.net", out string cached));
            Assert.Equal("Maria", cached);
        }

        [Fact]
        public void EnrichBatch_reuses_cache_without_second_resolve()
        {
            var cache = new SessionTipAuthorCache();
            int resolves = 0;
            Func<string, string> resolve = _ =>
            {
                resolves++;
                return "Joao";
            };

            OfflineReplayTipAuthorPrepare.EnrichBatch(
                new[]
                {
                    new OfflineReplayChatSummary { IsGroup = true, ParticipantJid = "222@s.whatsapp.net" },
                    new OfflineReplayChatSummary { IsGroup = true, ParticipantJid = "222@s.whatsapp.net" }
                },
                cache,
                jid => jid,
                resolve);

            Assert.Equal(1, resolves);
        }

        [Fact]
        public void PutOrUpdate_invalidates_stale_name_for_next_enrich()
        {
            var cache = new SessionTipAuthorCache();
            cache.PutOrUpdate("222@s.whatsapp.net", "Old");
            Assert.True(cache.PutOrUpdate("222@s.whatsapp.net", "New"));
            Assert.False(cache.PutOrUpdate("222@s.whatsapp.net", "New"));

            var summary = new OfflineReplayChatSummary
            {
                IsGroup = true,
                ParticipantJid = "222@s.whatsapp.net"
            };
            OfflineReplayTipAuthorPrepare.Enrich(
                summary,
                cache,
                jid => jid,
                jid => "should-not-call");

            Assert.Equal("New: ", summary.AuthorPrefix);
        }
    }
}
