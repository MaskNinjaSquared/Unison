using System;
using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BuildHistoryHydrationCandidatesDispatchTests
    {
        [Fact]
        public void Skips_empty_timelines()
        {
            var host = new FakeHost
            {
                Rows =
                {
                    new HistoryHydrationChatRow
                    {
                        Jid = "a@s.whatsapp.net",
                        LastMessageTimestampUtc = DateTime.UtcNow.AddHours(-12)
                    }
                },
                MessageCounts = new Dictionary<string, int>()
            };
            var result = BuildHistoryHydrationCandidatesDispatch.Run(host, DateTime.UtcNow);
            Assert.Empty(result);
        }

        [Fact]
        public void Selects_stale_chat_with_messages()
        {
            var host = new FakeHost
            {
                Rows =
                {
                    new HistoryHydrationChatRow
                    {
                        Jid = "5511888888888@s.whatsapp.net",
                        LastMessageTimestampUtc = DateTime.UtcNow.AddHours(-12)
                    }
                },
                MessageCounts =
                {
                    ["5511888888888@s.whatsapp.net"] = 5
                }
            };
            var result = BuildHistoryHydrationCandidatesDispatch.Run(host, DateTime.UtcNow);
            Assert.Single(result);
            Assert.Equal("5511888888888@s.whatsapp.net", result[0].Jid);
            Assert.True(result[0].IsStale);
        }

        private sealed class FakeHost : IBuildHistoryHydrationCandidatesHost
        {
            public List<HistoryHydrationChatRow> Rows { get; } =
                new List<HistoryHydrationChatRow>();

            public Dictionary<string, int> MessageCounts { get; set; } =
                new Dictionary<string, int>(StringComparer.Ordinal);

            public int GetHydrationChatCount() => Rows.Count;

            public HistoryHydrationChatRow GetHydrationChatAt(int index) => Rows[index];

            public string NormalizeHydrationJid(string jid) => jid;

            public int GetHydrationMessageCount(string jid) =>
                MessageCounts.TryGetValue(jid ?? string.Empty, out int n) ? n : 0;

            public TimeSpan HistoryBackfillStaleThreshold => TimeSpan.FromHours(8);

            public int HistoryBackfillMaxStaleTopChats => 40;
        }
    }
}
