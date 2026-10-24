using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class NotifyServerLogoutDispatchTests
    {
        [Fact]
        public async Task No_socket_is_noop()
        {
            var host = new FakeHost { HasSocket = false };
            await NotifyServerLogoutDispatch.RunAsync(host);
            Assert.False(host.Armed);
        }

        [Fact]
        public async Task Arms_monitor_and_logout()
        {
            var host = new FakeHost { HasSocket = true };
            await NotifyServerLogoutDispatch.RunAsync(host, "user");
            Assert.True(host.Armed);
            Assert.Equal("logout", host.HealthReason);
            Assert.Equal("user", host.LogoutReason);
        }

        [Fact]
        public async Task Logout_failure_is_recorded()
        {
            var host = new FakeHost { HasSocket = true, ThrowLogout = true };
            await NotifyServerLogoutDispatch.RunAsync(host);
            Assert.True(host.RecordedFail);
        }

        private sealed class FakeHost : INotifyServerLogoutHost
        {
            public bool HasSocket;
            public bool ThrowLogout;
            public bool Armed;
            public string HealthReason;
            public string LogoutReason;
            public bool RecordedFail;

            public bool HasActiveSocket => HasSocket;

            public void ArmUserLogoutDisconnectFlags() => Armed = true;

            public void StopConnectionHealthMonitorForLogout(string reason) =>
                HealthReason = reason;

            public Task LogoutActiveSocketAsync(string reason)
            {
                LogoutReason = reason;
                if (ThrowLogout)
                {
                    throw new InvalidOperationException("logout");
                }

                return Task.CompletedTask;
            }

            public void RecordLogoutNoticeFailed(Exception error) => RecordedFail = true;
        }
    }
}
