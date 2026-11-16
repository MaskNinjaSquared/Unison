using System.Collections.Generic;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class FullHistoryAckRejectedDispatchTests
    {
        [Fact]
        public void Rejected_during_catch_up_finishes_and_releases_enrichment()
        {
            var host = new FakeHost { CatchUpActive = true };

            FullHistoryAckRejectedDispatch.Run(host, "3EB0", "500", "message-store-epoch:after-pairing-bootstrap");

            Assert.Equal("full-history-ack-rejected", host.FinishReason);
            Assert.True(host.FinishReleasedEnrichment);
            Assert.True(host.UserResyncCompleted);
            Assert.True(host.StatusRaised);
        }

        [Fact]
        public void Rejected_without_catch_up_does_not_finish()
        {
            var host = new FakeHost { CatchUpActive = false };

            FullHistoryAckRejectedDispatch.Run(host, "3EB0", "500", "manual");

            Assert.Null(host.FinishReason);
            Assert.True(host.UserResyncCompleted);
        }

        [Fact]
        public void Rejection_is_journaled_with_request_error_and_trigger()
        {
            var host = new FakeHost { CatchUpActive = true };

            FullHistoryAckRejectedDispatch.Run(host, "3EB0", "500", "epoch");

            Assert.Single(host.Journal);
            Assert.Equal(
                "requestId=3EB0; error=500; trigger=epoch; catchUpActive=True",
                host.Journal[0]);
        }

        private sealed class FakeHost : IFullHistoryAckRejectedHost
        {
            public bool CatchUpActive;
            public string FinishReason;
            public bool FinishReleasedEnrichment;
            public bool UserResyncCompleted;
            public bool StatusRaised;
            public readonly List<string> Journal = new List<string>();

            public bool IsHistoryCatchUpActive => CatchUpActive;

            public void RaiseFullHistoryRejectedStatus() => StatusRaised = true;

            public void CompleteUserResyncOnFullHistoryAckRejected() => UserResyncCompleted = true;

            public void FinishHistoryCatchUp(string reason, bool releaseEnrichment)
            {
                FinishReason = reason;
                FinishReleasedEnrichment = releaseEnrichment;
            }

            public void WriteFullHistoryAckRejected(string detail) => Journal.Add(detail);
        }
    }
}
