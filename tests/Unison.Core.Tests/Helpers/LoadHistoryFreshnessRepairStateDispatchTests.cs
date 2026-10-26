using System;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class LoadHistoryFreshnessRepairStateDispatchTests
    {
        [Fact]
        public void Loads_both_timestamps()
        {
            var host = new FakeHost
            {
                RepairRaw = "2024-01-02T03:04:05.0000000Z",
                FallbackRaw = "2024-06-07T08:09:10.0000000Z"
            };
            LoadHistoryFreshnessRepairStateDispatch.Run(host);
            Assert.Equal(DateTime.Parse("2024-01-02T03:04:05.0000000Z").ToUniversalTime(), host.RepairUtc);
            Assert.Equal(DateTime.Parse("2024-06-07T08:09:10.0000000Z").ToUniversalTime(), host.FallbackUtc);
            Assert.Equal(2, host.LogCount);
        }

        [Fact]
        public void Read_failure_is_logged()
        {
            var host = new FakeHost { ThrowOnRead = true };
            LoadHistoryFreshnessRepairStateDispatch.Run(host);
            Assert.NotNull(host.LoggedError);
        }

        private sealed class FakeHost : ILoadHistoryFreshnessRepairStateHost
        {
            public string RepairRaw;
            public string FallbackRaw;
            public bool ThrowOnRead;
            public DateTime RepairUtc;
            public DateTime FallbackUtc;
            public int LogCount;
            public string LoggedError;

            public string ReadLastFullHistoryRepairCompletedUtcRaw()
            {
                if (ThrowOnRead)
                {
                    throw new InvalidOperationException("read");
                }

                return RepairRaw;
            }

            public string ReadLastFreshnessReconnectFallbackUtcRaw() => FallbackRaw;

            public void SetLastFullHistoryRepairCompletedUtc(DateTime timestampUtc) =>
                RepairUtc = timestampUtc;

            public void SetLastFreshnessReconnectFallbackUtc(DateTime timestampUtc) =>
                FallbackUtc = timestampUtc;

            public void LogLoadedFullHistoryRepairCompleted(DateTime timestampUtc) => LogCount++;

            public void LogLoadedFreshnessReconnectFallback(DateTime timestampUtc) => LogCount++;

            public void LogLoadHistoryFreshnessRepairStateFailed(string message) =>
                LoggedError = message;
        }
    }
}
