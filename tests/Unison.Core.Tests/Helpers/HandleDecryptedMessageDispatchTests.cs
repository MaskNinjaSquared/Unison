using System;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HandleDecryptedMessageDispatchTests
    {
        [Fact]
        public async Task Missing_facade_drops_without_throw()
        {
            var host = new FakeHost { HasFacade = false };
            await HandleDecryptedMessageDispatch.RunAsync(
                host,
                new IncomingDecryptedNotifyRequest { MessageId = "m1", IsOffline = true });
            Assert.False(host.Notified);
            Assert.True(host.MissingWritten);
        }

        [Fact]
        public async Task Routes_to_facade()
        {
            var host = new FakeHost { HasFacade = true };
            await HandleDecryptedMessageDispatch.RunAsync(
                host,
                new IncomingDecryptedNotifyRequest { MessageId = "m1", IsOffline = true });
            Assert.Equal("route", host.LastStage);
            Assert.True(host.Notified);
        }

        [Fact]
        public async Task Facade_fault_is_recorded_and_rethrows()
        {
            var host = new FakeHost { HasFacade = true, ThrowOnNotify = true };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                HandleDecryptedMessageDispatch.RunAsync(
                    host,
                    new IncomingDecryptedNotifyRequest { MessageId = "m1", IsOffline = true }));
            Assert.True(host.FailedRecorded);
        }

        private sealed class FakeHost : IHandleDecryptedMessageHost
        {
            public bool HasFacade;
            public bool ThrowOnNotify;
            public bool Notified;
            public bool MissingWritten;
            public bool FailedRecorded;
            public string LastStage;

            public bool HasMessageFacade => HasFacade;
            public string IncomingPumpStage => LastStage;

            public void SetIncomingMessagePumpStage(
                string stage,
                IncomingDecryptedNotifyRequest request) =>
                LastStage = stage;

            public void LogLiveInboundHandle(IncomingDecryptedNotifyRequest request)
            {
            }

            public void WriteMessageFacadeMissing(string messageId) => MissingWritten = true;

            public Task NotifyIncomingDecryptedAsync(IncomingDecryptedNotifyRequest request)
            {
                if (ThrowOnNotify)
                {
                    throw new InvalidOperationException("boom");
                }

                Notified = true;
                return Task.CompletedTask;
            }

            public void LogHandleDecryptedError(
                Exception error,
                IncomingDecryptedNotifyRequest request)
            {
            }

            public void RecordHandleDecryptedFailed(
                Exception error,
                IncomingDecryptedNotifyRequest request) =>
                FailedRecorded = true;
        }
    }
}
