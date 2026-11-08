using Unison.Core.State;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class SessionTipAuthorCacheTests
    {
        [Fact]
        public void PutOrUpdate_returns_true_only_when_value_changes()
        {
            var cache = new SessionTipAuthorCache();
            Assert.True(cache.PutOrUpdate("a@s.whatsapp.net", "Ana"));
            Assert.False(cache.PutOrUpdate("a@s.whatsapp.net", "Ana"));
            Assert.True(cache.PutOrUpdate("a@s.whatsapp.net", "Ana Silva"));
            Assert.True(cache.TryGet("a@s.whatsapp.net", out string name));
            Assert.Equal("Ana Silva", name);
        }

        [Fact]
        public void Invalidate_drops_entry()
        {
            var cache = new SessionTipAuthorCache();
            cache.PutOrUpdate("a@s.whatsapp.net", "Ana");
            cache.Invalidate("a@s.whatsapp.net");
            Assert.False(cache.TryGet("a@s.whatsapp.net", out _));
        }
    }
}
