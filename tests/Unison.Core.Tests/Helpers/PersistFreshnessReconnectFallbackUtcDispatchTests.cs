using System;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class PersistFreshnessReconnectFallbackUtcDispatchTests
    {
        [Fact]
        public void Writes_roundtrip_timestamp()
        {
            var host = new FakeHost();
            var utc = new DateTime(2024, 8, 9, 10, 11, 12, DateTimeKind.Utc);
            PersistFreshnessReconnectFallbackUtcDispatch.Run(host, utc);
            Assert.Equal(utc, host.SetUtc);
            Assert.Equal(utc.ToString("O"), host.Written);
        }

        [Fact]
        public void Write_failure_is_logged()
        {
            var host = new FakeHost { ThrowOnWrite = true };
            PersistFreshnessReconnectFallbackUtcDispatch.Run(host, DateTime.UtcNow);
            Assert.NotNull(host.LoggedError);
        }

        private sealed class FakeHost : IPersistFreshnessReconnectFallbackUtcHost
        {
            public bool ThrowOnWrite;
            public DateTime SetUtc;
            public string Written;
            public string LoggedError;

            public void SetLastFreshnessReconnectFallbackUtc(DateTime timestampUtc) =>
                SetUtc = timestampUtc;

            public void WriteLastFreshnessReconnectFallbackUtc(string roundtripText)
            {
                if (ThrowOnWrite)
                {
                    throw new InvalidOperationException("write");
                }

                Written = roundtripText;
            }

            public void LogPersistFreshnessReconnectFallbackFailed(string message) =>
                LoggedError = message;
        }
    }
}
