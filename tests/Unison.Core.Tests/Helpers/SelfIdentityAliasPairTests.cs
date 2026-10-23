using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SelfIdentityAliasPairTests
    {
        [Fact]
        public void Accepts_distinct_normalized_pair()
        {
            Assert.True(SelfIdentityAliasPair.TryCreate(
                " 5511@s.whatsapp.net ",
                "199@lid",
                s => s.Trim(),
                out string id,
                out string lid));
            Assert.Equal("5511@s.whatsapp.net", id);
            Assert.Equal("199@lid", lid);
        }

        [Fact]
        public void Rejects_same_or_blank()
        {
            Assert.False(SelfIdentityAliasPair.TryCreate(
                "a@lid",
                "a@lid",
                s => s,
                out _,
                out _));
            Assert.False(SelfIdentityAliasPair.TryCreate(
                null,
                "a@lid",
                s => s,
                out _,
                out _));
        }
    }
}
