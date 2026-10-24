using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryHydrationBackfillDispatchTests
    {
        [Fact]
        public async Task Returns_zero_when_not_connected()
        {
            var host = new FakeHost { Connected = false };
            int requested = await HistoryHydrationBackfillDispatch.RunAsync(host, "test");
            Assert.Equal(0, requested);
            Assert.True(host.SkippedNotConnected);
        }

        [Fact]
        public async Task Returns_zero_when_no_candidates()
        {
            var host = new FakeHost { Connected = true };
            int requested = await HistoryHydrationBackfillDispatch.RunAsync(host, "test");
            Assert.Equal(0, requested);
            Assert.True(host.NoCandidates);
        }

        [Fact]
        public async Task Requests_one_page_then_stops_when_ledger_empty()
        {
            var host = new FakeHost
            {
                Connected = true,
                Candidates =
                {
                    new HistoryBackfillCandidate
                    {
                        Jid = "5511888888888@s.whatsapp.net",
                        ListIndex = 0,
                        MessageCount = 3,
                        IsStale = true
                    }
                }
            };
            host.MessageCounts["5511888888888@s.whatsapp.net"] = 3;

            int requested = await HistoryHydrationBackfillDispatch.RunAsync(host, "test");

            Assert.Equal(1, requested);
            Assert.Equal(1, host.RequestCalls);
            Assert.True(host.WroteStart);
            Assert.True(host.WroteDone);
        }

        private sealed class FakeHost : IHistoryHydrationBackfillHost
        {
            public bool Connected { get; set; }
            public List<HistoryBackfillCandidate> Candidates { get; } =
                new List<HistoryBackfillCandidate>();
            public Dictionary<string, int> MessageCounts { get; } =
                new Dictionary<string, int>(StringComparer.Ordinal);
            public bool SkippedNotConnected { get; private set; }
            public bool NoCandidates { get; private set; }
            public bool WroteStart { get; private set; }
            public bool WroteDone { get; private set; }
            public int RequestCalls { get; private set; }

            public bool IsConnected => Connected;
            public int HydrationBatchSize => 10;
            public int HydrationPageSize => 50;
            public int HydrationMaxPagesPerChat => 5;

            public IReadOnlyList<HistoryBackfillCandidate> BuildHydrationCandidates(DateTime nowUtc) =>
                Candidates;

            public int GetHydrationMessageCount(string jid) =>
                MessageCounts.TryGetValue(jid ?? string.Empty, out int n) ? n : 0;

            public string NormalizeHydrationJid(string jid) => jid;

            public Task<bool> RequestHydrationOnDemandAsync(
                string normJid,
                int pageSize,
                string triggerReason)
            {
                RequestCalls++;
                return Task.FromResult(true);
            }

            public Task WaitForHydrationSettleAsync() => Task.CompletedTask;

            public void LogHydrationSkippedNotConnected(string reason) =>
                SkippedNotConnected = true;

            public void LogHydrationNoCandidates(string reason) => NoCandidates = true;

            public void WriteHydrationStart(
                string reason,
                int chatCount,
                int batchSize,
                int maxPages) =>
                WroteStart = true;

            public void WriteHydrationBatch(
                int batchIndex,
                int sentCount,
                int ramGrew,
                int hasMoreCount,
                HistoryBackfillContinueAction action)
            {
            }

            public void LogHydrationDone(
                int requested,
                int synced,
                int hasMoreLeft,
                int failed) =>
                WroteDone = true;
        }
    }
}
