using Unison.Core.Helpers.Chats;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class MessagePreviewReconcilePendingTests
    {
        [Fact]
        public void Add_dedupes_case_insensitive()
        {
            var pending = new MessagePreviewReconcilePending();
            pending.Add("5511@s.whatsapp.net");
            pending.Add("5511@S.WHATSAPP.NET");
            pending.Add("5511@s.whatsapp.net");

            Assert.Equal(1, pending.Count);
            Assert.Equal(new[] { "5511@s.whatsapp.net" }, pending.Take());
            Assert.True(pending.IsEmpty);
        }

        [Fact]
        public void Take_returns_empty_when_none()
        {
            var pending = new MessagePreviewReconcilePending();
            Assert.Empty(pending.Take());
        }
    }
}
