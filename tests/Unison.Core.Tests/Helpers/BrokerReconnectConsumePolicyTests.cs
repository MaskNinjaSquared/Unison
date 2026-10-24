using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class BrokerReconnectConsumePolicyTests
    {
        [Fact]
        public void Consumes_when_reconnect_required()
        {
            Assert.True(
                BrokerReconnectConsumePolicy.ShouldConsume(
                    true,
                    false,
                    DateTime.UtcNow,
                    DateTime.UtcNow,
                    true));
        }

        [Fact]
        public void Consumes_when_broker_owner_stale_and_socket_gone()
        {
            DateTime now = DateTime.UtcNow;
            Assert.True(
                BrokerReconnectConsumePolicy.ShouldConsume(
                    false,
                    true,
                    now - TimeSpan.FromSeconds(4),
                    now,
                    false));
        }

        [Fact]
        public void Skips_when_broker_fresh_or_socket_still_listed()
        {
            DateTime now = DateTime.UtcNow;
            Assert.False(
                BrokerReconnectConsumePolicy.ShouldConsume(
                    false,
                    true,
                    now - TimeSpan.FromSeconds(1),
                    now,
                    false));
            Assert.False(
                BrokerReconnectConsumePolicy.ShouldConsume(
                    false,
                    true,
                    now - TimeSpan.FromSeconds(10),
                    now,
                    true));
            Assert.False(
                BrokerReconnectConsumePolicy.ShouldConsume(
                    false,
                    false,
                    now - TimeSpan.FromSeconds(10),
                    now,
                    false));
        }
    }
}
