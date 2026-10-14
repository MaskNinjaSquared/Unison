// =============================================================================
// Tests for BrokerHandoffHold.
//
// Whether the app keeps its socket instead of handing it to the background
// broker on entered-background / suspending. The broker never sends the
// WhatsApp keep-alive, so a first sync handed to it can stall halfway.
// =============================================================================
using Unison.Core.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class BrokerHandoffHoldTests
    {
        [Fact]
        public void Nothing_holds_the_socket_back_once_the_first_sync_is_over()
        {
            Assert.Null(BrokerHandoffHold.ReasonToKeepSocket(
                keepsSocketInProcess: false,
                isInitialSyncActive: false));
        }

        [Fact]
        public void A_first_sync_still_arriving_keeps_the_socket_in_process()
        {
            Assert.Equal(
                BrokerHandoffHold.InitialSync,
                BrokerHandoffHold.ReasonToKeepSocket(
                    keepsSocketInProcess: false,
                    isInitialSyncActive: true));
        }

        [Fact]
        public void Background_mode_keeps_the_socket_whatever_the_sync_is_doing()
        {
            Assert.Equal(
                BrokerHandoffHold.LocationKeepAlive,
                BrokerHandoffHold.ReasonToKeepSocket(
                    keepsSocketInProcess: true,
                    isInitialSyncActive: false));
        }

        [Fact]
        public void Background_mode_is_the_reason_reported_when_both_hold()
        {
            Assert.Equal(
                BrokerHandoffHold.LocationKeepAlive,
                BrokerHandoffHold.ReasonToKeepSocket(
                    keepsSocketInProcess: true,
                    isInitialSyncActive: true));
        }

        [Fact]
        public void The_reasons_read_as_the_diagnostics_journal_already_spells_them()
        {
            Assert.Equal("location-keepalive", BrokerHandoffHold.LocationKeepAlive);
            Assert.Equal("initial-sync", BrokerHandoffHold.InitialSync);
        }
    }
}
