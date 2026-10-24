using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PublishConnectionUpdateDispatchTests
    {
        [Fact]
        public void Sets_status_and_raises()
        {
            var host = new FakeHost();
            PublishConnectionUpdateDispatch.Run(host, "open");
            Assert.Equal("open", host.CurrentConnectionStatus);
            Assert.True(host.Raised);
            Assert.True(host.Stamped);
            Assert.False(host.ToastCleared);
        }

        [Fact]
        public void Clears_toast_when_policy_says_so()
        {
            var host = new FakeHost { ClearToast = true };
            PublishConnectionUpdateDispatch.Run(host, "connected");
            Assert.True(host.ToastCleared);
            Assert.False(host.SuppressToast);
        }

        private sealed class FakeHost : IPublishConnectionUpdateHost
        {
            public bool ClearToast;
            public bool SuppressToast = true;
            public bool ToastCleared;
            public bool Stamped;
            public bool Raised;
            public string CurrentConnectionStatus { get; set; }

            public bool ShouldClearReconnectRequiredToast(string status) => ClearToast;

            public void SetSuppressReconnectToast(bool suppress) => SuppressToast = suppress;

            public void ClearReconnectRequiredToast(string status) => ToastCleared = true;

            public void StampLastConnectionEventUtc() => Stamped = true;

            public void WriteConnectionStatusJournal(string status)
            {
            }

            public void LogConnectionStatus(string status)
            {
            }

            public void RaiseConnectionUpdate(string status) => Raised = true;
        }
    }
}
