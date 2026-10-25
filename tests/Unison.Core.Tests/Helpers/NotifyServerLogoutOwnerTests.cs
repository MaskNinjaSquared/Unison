using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class NotifyServerLogoutOwnerTests
    {
        [Fact]
        public async Task Run_skips_without_socket()
        {
            bool armed = false;
            var owner = new NotifyServerLogoutOwner(
                () => false,
                () => armed = true,
                _ => { },
                _ => Task.CompletedTask);

            await owner.RunAsync("test");

            Assert.False(armed);
        }

        [Fact]
        public async Task Run_arms_stops_and_logs_out()
        {
            bool armed = false;
            string stopped = null;
            string loggedOut = null;
            var owner = new NotifyServerLogoutOwner(
                () => true,
                () => armed = true,
                reason => stopped = reason,
                reason =>
                {
                    loggedOut = reason;
                    return Task.CompletedTask;
                });

            await owner.RunAsync("user");

            Assert.True(armed);
            Assert.Equal("logout", stopped);
            Assert.Equal("user", loggedOut);
        }

        [Fact]
        public async Task Run_records_logout_failure()
        {
            Exception recorded = null;
            var owner = new NotifyServerLogoutOwner(
                () => true,
                () => { },
                _ => { },
                _ => throw new InvalidOperationException("gone"),
                ex => recorded = ex);

            await owner.RunAsync();

            Assert.NotNull(recorded);
            Assert.Equal("gone", recorded.Message);
        }
    }
}
