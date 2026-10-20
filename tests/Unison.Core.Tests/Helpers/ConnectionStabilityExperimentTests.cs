using System;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    [Collection(ConnectionStabilityExperimentCollection.Name)]
    public class ConnectionStabilityExperimentTests : IDisposable
    {
        private readonly bool _wasEnabled;

        public ConnectionStabilityExperimentTests()
        {
            _wasEnabled = ConnectionStabilityExperiment.Enabled;
        }

        public void Dispose()
        {
            ConnectionStabilityExperiment.Enabled = _wasEnabled;
        }

        [Fact]
        public void When_enabled_promotes_failed_bubble_after_accept()
        {
            ConnectionStabilityExperiment.Enabled = true;

            Assert.True(ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(ChatMessage.StatusPending));
            Assert.True(ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(ChatMessage.StatusFailed));
            Assert.False(ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(ChatMessage.StatusSent));
            Assert.Equal(3, ConnectionStabilityExperiment.TextSendMaxAttempts);
            Assert.Equal(TimeSpan.FromSeconds(2), ConnectionStabilityExperiment.TextSendRetryDelay);
            Assert.True(ConnectionStabilityExperiment.PreferStreamSocketOnMobile);
            Assert.True(ConnectionStabilityExperiment.UseDeferAwareHealthMonitor);
            Assert.False(ConnectionStabilityExperiment.TreatQueryTimeoutAsConnectionLost);
        }

        [Fact]
        public void When_disabled_keeps_legacy_send_and_health_defaults()
        {
            ConnectionStabilityExperiment.Enabled = false;

            Assert.True(ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(ChatMessage.StatusPending));
            Assert.False(ConnectionStabilityExperiment.ShouldPromoteBubbleAfterAccept(ChatMessage.StatusFailed));
            Assert.Equal(2, ConnectionStabilityExperiment.TextSendMaxAttempts);
            Assert.Equal(TimeSpan.FromMilliseconds(500), ConnectionStabilityExperiment.TextSendRetryDelay);
            Assert.False(ConnectionStabilityExperiment.PreferStreamSocketOnMobile);
            Assert.False(ConnectionStabilityExperiment.UseDeferAwareHealthMonitor);
            Assert.True(ConnectionStabilityExperiment.TreatQueryTimeoutAsConnectionLost);
            Assert.Equal(TimeSpan.Zero, ConnectionStabilityExperiment.KeepAliveSilenceFloor);
        }

        [Fact]
        public void Settlement_helper_follows_the_experiment_switch()
        {
            ConnectionStabilityExperiment.Enabled = true;
            Assert.True(OutgoingSendSettlement.ShouldPromoteAfterTransportAccept(ChatMessage.StatusFailed));

            ConnectionStabilityExperiment.Enabled = false;
            Assert.False(OutgoingSendSettlement.ShouldPromoteAfterTransportAccept(ChatMessage.StatusFailed));
        }
    }
}
