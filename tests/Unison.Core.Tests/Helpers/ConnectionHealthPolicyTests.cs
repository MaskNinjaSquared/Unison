// =============================================================================
// Tests for ConnectionHealthPolicy.
//
// This decides when a live socket gets torn down. Getting it wrong is expensive
// in both directions: too eager and the app reconnects on a working connection,
// too slow and the user sits in front of a socket that stopped delivering.
//
// The threshold tests are the unusual ones here. They do not assert that a
// number is what it is -- that would just restate the source. They assert the
// relationships between the numbers that have to hold for the policy to work at
// all, which is the part nobody would notice breaking.
// =============================================================================
using System;
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ConnectionHealthPolicyTests
    {
        // --- The decision ------------------------------------------------------

        [Fact]
        public void A_fresh_socket_is_left_alone()
        {
            Assert.Equal(
                ConnectionHealthAction.Idle,
                ConnectionHealthPolicy.Evaluate(nodeProcessingStalled: false, connectionIsFresh: true));
        }

        [Fact]
        public void A_quiet_socket_is_asked_to_prove_it_is_alive()
        {
            // Silence alone is not failure -- nothing may have been sent. Probe before
            // throwing away a connection that might be fine.
            Assert.Equal(
                ConnectionHealthAction.Probe,
                ConnectionHealthPolicy.Evaluate(nodeProcessingStalled: false, connectionIsFresh: false));
        }

        [Fact]
        public void A_stalled_queue_is_never_probed_it_goes_straight_to_reconnect()
        {
            // The heart of this policy. A socket whose ordered node queue has stopped
            // making progress still answers probes, so probing it would come back healthy
            // and the stall would survive the health check -- while user messages never
            // reach the app. The only way out is a new socket.
            Assert.Equal(
                ConnectionHealthAction.Reconnect,
                ConnectionHealthPolicy.Evaluate(nodeProcessingStalled: true, connectionIsFresh: false));
        }

        [Fact]
        public void Fresh_frames_do_not_excuse_a_stalled_queue()
        {
            // The dangerous case: traffic is arriving, so every surface signal says the
            // connection is healthy. It is not. Freshness must not outrank the stall.
            Assert.Equal(
                ConnectionHealthAction.Reconnect,
                ConnectionHealthPolicy.Evaluate(nodeProcessingStalled: true, connectionIsFresh: true));
        }

        // --- Backoff -------------------------------------------------------------

        [Fact]
        public void The_first_retry_is_quick()
        {
            // A dropped connection is usually transient; making the user wait 30s for the
            // first attempt would be the wrong trade.
            Assert.Equal(TimeSpan.FromSeconds(1), ConnectionHealthPolicy.ReconnectDelay(0));
        }

        [Fact]
        public void Each_attempt_waits_at_least_as_long_as_the_one_before()
        {
            // Monotonic, or the backoff is not a backoff.
            for (int attempt = 1; attempt < ConnectionHealthPolicy.ReconnectLadderLength; attempt++)
            {
                Assert.True(
                    ConnectionHealthPolicy.ReconnectDelay(attempt) >= ConnectionHealthPolicy.ReconnectDelay(attempt - 1),
                    "attempt " + attempt + " waits less than attempt " + (attempt - 1));
            }
        }

        [Fact]
        public void The_delay_stops_growing_instead_of_running_away()
        {
            // A phone that has been offline overnight must still retry every 30s, not once
            // an hour.
            TimeSpan last = ConnectionHealthPolicy.ReconnectDelay(ConnectionHealthPolicy.ReconnectLadderLength - 1);

            Assert.Equal(last, ConnectionHealthPolicy.ReconnectDelay(ConnectionHealthPolicy.ReconnectLadderLength));
            Assert.Equal(last, ConnectionHealthPolicy.ReconnectDelay(10000));
        }

        [Fact]
        public void A_nonsense_attempt_number_does_not_crash()
        {
            // The previous code indexed the ladder with Math.Min(attempt, length - 1),
            // which throws on a negative attempt instead of clamping.
            Assert.Equal(ConnectionHealthPolicy.ReconnectDelay(0), ConnectionHealthPolicy.ReconnectDelay(-1));
            Assert.Equal(ConnectionHealthPolicy.ReconnectDelay(0), ConnectionHealthPolicy.ReconnectDelay(int.MinValue));
        }

        // --- Relationships between the thresholds ----------------------------------

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void The_monitor_looks_more_often_than_a_connection_takes_to_go_stale(bool onDemand)
        {
            // If the check interval were the longer of the two, every single cycle would
            // find the connection stale and probe it, turning a health check into a
            // constant keep-alive. The gap has to leave room for at least one quiet cycle.
            ConnectionHealthProfile profile = onDemand
                ? ConnectionHealthProfile.OnDemand
                : ConnectionHealthProfile.Background;

            Assert.True(
                ConnectionHealthPolicy.CheckInterval < profile.FreshnessLimit,
                "the monitor would probe on every cycle");
        }

        [Fact]
        public void A_connection_is_called_stale_long_before_the_queue_is_called_stalled()
        {
            // Staleness is recoverable and costs one probe; a stall costs the whole socket.
            // The cheap diagnosis has to get its chance first.
            Assert.True(
                ConnectionHealthProfile.Background.FreshnessLimit < ConnectionHealthPolicy.NodeProcessingStallLimit,
                "the socket would be torn down before a probe was ever tried");
        }

        [Fact]
        public void The_on_demand_check_is_the_stricter_of_the_two()
        {
            // With the user waiting on the result, acting on a dead socket costs more than
            // the probe does. In the background the trade runs the other way, because that
            // one is paid for on battery every cycle.
            Assert.True(
                ConnectionHealthProfile.OnDemand.FreshnessLimit < ConnectionHealthProfile.Background.FreshnessLimit,
                "the on-demand check tolerates more staleness than the background monitor");

            Assert.True(
                ConnectionHealthProfile.OnDemand.ProbeTimeoutMs >= ConnectionHealthProfile.Background.ProbeTimeoutMs,
                "the on-demand check gives the probe less time than the background monitor");
        }

        [Fact]
        public void The_message_pump_is_given_up_on_sooner_than_the_socket_is()
        {
            // A stalled pump is restarted on its own; a stalled socket is replaced. Since
            // the cheap recovery cannot fix the expensive failure, it has to be tried
            // first, which only works if its limit is the shorter one.
            Assert.True(
                ConnectionHealthPolicy.IncomingPumpStallLimit < ConnectionHealthPolicy.NodeProcessingStallLimit,
                "the socket would be torn down before the pump was ever restarted");
        }

        [Fact]
        public void Every_probe_has_time_to_answer_within_one_monitor_cycle()
        {
            // A probe that outlived the interval would still be waiting when the next cycle
            // began, stacking checks on top of each other.
            Assert.True(
                ConnectionHealthProfile.Background.ProbeTimeoutMs < ConnectionHealthPolicy.CheckInterval.TotalMilliseconds,
                "a probe can outlive the cycle that started it");
        }
    }
}
