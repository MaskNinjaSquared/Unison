using System;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class HistoryFreshnessUtcTextTests
    {
        [Fact]
        public void Parses_roundtrip_utc()
        {
            Assert.True(
                HistoryFreshnessUtcText.TryParseRoundtrip(
                    "2024-01-02T03:04:05.0000000Z",
                    out DateTime utc));
            Assert.Equal(DateTimeKind.Utc, utc.Kind);
        }

        [Fact]
        public void Rejects_empty()
        {
            Assert.False(HistoryFreshnessUtcText.TryParseRoundtrip("", out _));
        }
    }
}
