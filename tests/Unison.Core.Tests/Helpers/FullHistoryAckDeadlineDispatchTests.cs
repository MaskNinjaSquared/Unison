using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.State;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullHistoryAckDeadlineDispatchTests
    {
        [Fact]
        public async Task Accepted_request_is_left_to_the_watchdog()
        {
            var host = new FakeHost { Tracked = Request(ackAccepted: true), BannerActive = true };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.Equal(TimeSpan.FromSeconds(30), host.Delayed);
            Assert.False(host.Cleared);
            Assert.Null(host.FinishReason);
            Assert.Empty(host.Journal);
        }

        [Fact]
        public async Task Unacked_request_is_cleared_and_finishes_the_catch_up()
        {
            var host = new FakeHost { Tracked = Request(ackAccepted: false), BannerActive = true };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.True(host.Cleared);
            Assert.Equal(FullHistoryAckDeadlineDispatch.AckMissingReason, host.FinishReason);
            Assert.True(host.ReleaseEnrichment);
            Assert.Contains(host.Journal, e => e.StartsWith(FullHistoryAckDeadlineDispatch.AckMissingReason + "|requestId=r1"));
        }

        [Fact]
        public async Task Unacked_request_without_banner_is_only_cleared()
        {
            var host = new FakeHost { Tracked = Request(ackAccepted: false), BannerActive = false };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.True(host.Cleared);
            Assert.Null(host.FinishReason);
        }

        [Fact]
        public async Task Banner_left_with_no_full_request_behind_it_is_finished()
        {
            var host = new FakeHost { Tracked = null, FullPending = false, BannerActive = true };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.False(host.Cleared);
            Assert.Equal(FullHistoryAckDeadlineDispatch.OrphanedReason, host.FinishReason);
            Assert.True(host.ReleaseEnrichment);
            Assert.Contains(host.Journal, e => e.StartsWith(FullHistoryAckDeadlineDispatch.OrphanedReason + "|requestId=r1"));
        }

        [Fact]
        public async Task Another_full_request_in_flight_owns_the_banner()
        {
            var host = new FakeHost { Tracked = null, FullPending = true, BannerActive = true };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.Null(host.FinishReason);
            Assert.Empty(host.Journal);
        }

        [Fact]
        public async Task Nothing_to_do_when_request_and_banner_are_both_gone()
        {
            var host = new FakeHost { Tracked = null, FullPending = false, BannerActive = false };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.Null(host.FinishReason);
            Assert.Empty(host.Journal);
        }

        [Fact]
        public async Task Request_answered_between_lookup_and_clear_is_not_finished_here()
        {
            var host = new FakeHost
            {
                Tracked = Request(ackAccepted: false),
                BannerActive = true,
                ClearSucceeds = false
            };

            await FullHistoryAckDeadlineDispatch.RunAsync(host, "r1");

            Assert.Null(host.FinishReason);
        }

        private static HistoryOnDemandRequest Request(bool ackAccepted)
        {
            var registry = new HistoryOnDemandRegistry();
            DateTime now = DateTime.UtcNow;
            HistoryOnDemandRequest request = registry.RegisterFull(
                "r1",
                isRepair: true,
                baselineMessageCount: 0,
                reason: "message-store-epoch:after-pairing-bootstrap",
                nowUtc: now);
            if (ackAccepted)
            {
                registry.Ack("r1", null, now);
            }

            return request;
        }

        private sealed class FakeHost : IFullHistoryAckDeadlineHost
        {
            public HistoryOnDemandRequest Tracked;
            public bool FullPending;
            public bool BannerActive;
            public bool ClearSucceeds = true;
            public bool Cleared;
            public TimeSpan Delayed;
            public string FinishReason;
            public bool ReleaseEnrichment;
            public readonly List<string> Journal = new List<string>();

            public TimeSpan FullHistoryAckDeadline => TimeSpan.FromSeconds(30);

            public Task DelayAsync(TimeSpan delay)
            {
                Delayed = delay;
                return Task.CompletedTask;
            }

            public HistoryOnDemandRequest FindFullRequest(string requestId) => Tracked;

            public bool TryClearFullRequest(HistoryOnDemandRequest request)
            {
                Cleared = ClearSucceeds;
                return ClearSucceeds;
            }

            public bool IsFullHistoryPending => FullPending;

            public bool IsHistoryCatchUpBannerActive => BannerActive;

            public void FinishHistoryCatchUp(string reason, bool releaseEnrichment)
            {
                FinishReason = reason;
                ReleaseEnrichment = releaseEnrichment;
            }

            public void WriteHistoryJournal(string eventName, string detail) =>
                Journal.Add(eventName + "|" + detail);
        }
    }
}
