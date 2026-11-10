using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UserLogoutDispatchTests
    {
        [Fact]
        public async Task Timeout_still_clears_session()
        {
            var host = new FakeHost
            {
                NotifyDelay = Task.Delay(60_000)
            };

            await UserLogoutDispatch.RunAsync(
                host,
                "user",
                notifyTimeout: TimeSpan.FromMilliseconds(50));

            Assert.Contains("logout-notify-begin", host.Events);
            Assert.Contains("logout-notify-timeout", host.Events);
            Assert.Contains("clear-session-begin", host.Events);
            Assert.Equal(1, host.ClearCount);
            Assert.Equal(1, host.ContactsClearCount);
            Assert.Equal(1, host.RestoreCount);
            Assert.False(host.Busy);
        }

        [Fact]
        public async Task Reentry_while_busy_is_ignored()
        {
            var notifyStarted = new TaskCompletionSource<bool>();
            var allowNotify = new TaskCompletionSource<bool>();
            var host = new FakeHost
            {
                NotifyDelay = WaitAndSignal(notifyStarted, allowNotify.Task)
            };

            Task first = UserLogoutDispatch.RunAsync(host, "user");
            await notifyStarted.Task;

            await UserLogoutDispatch.RunAsync(host, "again");

            Assert.Contains("logout-already-in-flight", host.Events);
            Assert.Equal(0, host.ClearCount);

            allowNotify.TrySetResult(true);
            await first;

            Assert.Equal(1, host.ClearCount);
            Assert.False(host.Busy);
        }

        [Fact]
        public async Task Happy_path_clears_once()
        {
            var host = new FakeHost();

            await UserLogoutDispatch.RunAsync(host, "user-initiated");

            Assert.Contains("logout-notify-begin", host.Events);
            Assert.Contains("logout-notify-end", host.Events);
            Assert.Contains("clear-session-begin", host.Events);
            Assert.DoesNotContain("logout-notify-timeout", host.Events);
            Assert.Equal("user-initiated", host.NotifyReason);
            Assert.Equal(1, host.ClearCount);
            Assert.Equal(1, host.ContactsClearCount);
            Assert.Equal(1, host.RestoreCount);
            Assert.False(host.Busy);
        }

        private static async Task WaitAndSignal(
            TaskCompletionSource<bool> started,
            Task gate)
        {
            started.TrySetResult(true);
            await gate.ConfigureAwait(false);
        }

        private sealed class FakeHost : IUserLogoutHost
        {
            private int _busy;

            public Task NotifyDelay = Task.CompletedTask;
            public string NotifyReason;
            public int ClearCount;
            public int ContactsClearCount;
            public int RestoreCount;
            public bool Busy => _busy != 0;
            public List<string> Events { get; } = new List<string>();

            public bool TryBeginLogout()
            {
                if (System.Threading.Interlocked.Exchange(ref _busy, 1) == 1)
                {
                    return false;
                }

                return true;
            }

            public void EndLogout() =>
                System.Threading.Interlocked.Exchange(ref _busy, 0);

            public void JournalLogout(string eventName, string detail = null) =>
                Events.Add(eventName);

            public async Task NotifyServerLogoutAsync(string reason)
            {
                NotifyReason = reason;
                await NotifyDelay.ConfigureAwait(false);
            }

            public Task ClearSessionAsync()
            {
                ClearCount++;
                return Task.CompletedTask;
            }

            public Task ClearPublishedContactsAsync()
            {
                ContactsClearCount++;
                return Task.CompletedTask;
            }

            public Task AutoRestoreDefaultAsync()
            {
                RestoreCount++;
                return Task.CompletedTask;
            }
        }
    }
}
