using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    /// <summary>
    /// Pins the fixed FULL / on-demand / hydration timings. Retuning any of these is a
    /// Mobile behaviour change that needs a device run, not a refactor.
    /// </summary>
    public sealed class HistoryOnDemandPacingTests
    {
        [Fact]
        public void On_demand_and_full_history_timings_are_unchanged()
        {
            Assert.Equal(TimeSpan.FromSeconds(20), HistoryOnDemandPacing.OnDemandResponseTimeout);
            Assert.Equal(TimeSpan.FromMinutes(15), HistoryOnDemandPacing.FullHistoryResponseTimeout);
            Assert.Equal(TimeSpan.FromSeconds(8), HistoryOnDemandPacing.FullHistoryCatchUpProgressPoll);
        }

        [Fact]
        public void Freshness_and_hydration_timings_are_unchanged()
        {
            Assert.Equal(TimeSpan.FromMinutes(30), HistoryOnDemandPacing.FreshnessStaleThreshold);
            Assert.Equal(TimeSpan.FromHours(8), HistoryOnDemandPacing.BackfillStaleThreshold);
            Assert.Equal(40, HistoryOnDemandPacing.BackfillMaxStaleTopChats);
            Assert.Equal(TimeSpan.FromSeconds(25), HistoryOnDemandPacing.HydrationSettleBudget);
            Assert.Equal(10, HistoryOnDemandPacing.HydrationBatchSize);
            Assert.Equal(50, HistoryOnDemandPacing.HydrationPageSize);
            Assert.Equal(5, HistoryOnDemandPacing.HydrationMaxPagesPerChat);
        }
    }
}
