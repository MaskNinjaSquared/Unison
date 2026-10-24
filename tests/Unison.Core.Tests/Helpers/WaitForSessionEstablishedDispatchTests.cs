using System;
using System.Threading.Tasks;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class WaitForSessionEstablishedDispatchTests
    {
        [Fact]
        public async Task Waits_gate_successfully()
        {
            var host = new FakeHost();
            await WaitForSessionEstablishedDispatch.RunAsync(host, "pump");
            Assert.Equal("pump", host.WaitingReason);
            Assert.True(host.Waited);
        }

        [Fact]
        public async Task Failed_gate_logs_and_rethrows()
        {
            var host = new FakeHost { Fail = true };
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => WaitForSessionEstablishedDispatch.RunAsync(host, "x"));
            Assert.NotNull(host.AbortError);
        }

        private sealed class FakeHost : IWaitForSessionEstablishedHost
        {
            public bool Fail;
            public bool Waited;
            public string WaitingReason;
            public Exception AbortError;

            public void LogWaitingForSession(string reason) => WaitingReason = reason;

            public Task WaitSessionEstablishedGateAsync()
            {
                Waited = true;
                if (Fail)
                {
                    return Task.FromException(new InvalidOperationException("gate"));
                }

                return Task.CompletedTask;
            }

            public void LogSessionGateAborted(string reason, Exception error) =>
                AbortError = error;
        }
    }
}
