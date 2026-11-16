using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionSessionFlagsTests
    {
        [Fact]
        public void A_new_session_starts_with_every_flag_down()
        {
            var flags = new ConnectionSessionFlags();

            Assert.False(flags.SuppressReconnect);
            Assert.False(flags.FatalSessionEnded);
            Assert.False(flags.SessionEstablishedThisConnection);
            Assert.False(flags.PairingRestartPending);
            Assert.False(flags.CountPreSessionCloseAsFatal);
            Assert.False(flags.QrDeliveredThisConnection);
            Assert.Equal(0, flags.PreSessionCloseStreak);
        }

        [Fact]
        public void Increment_returns_the_new_streak_and_reset_returns_the_old_one()
        {
            var flags = new ConnectionSessionFlags();

            Assert.Equal(1, flags.IncrementPreSessionCloseStreak());
            Assert.Equal(2, flags.IncrementPreSessionCloseStreak());
            Assert.Equal(2, flags.ResetPreSessionCloseStreak());
            Assert.Equal(0, flags.PreSessionCloseStreak);
        }
    }
}
