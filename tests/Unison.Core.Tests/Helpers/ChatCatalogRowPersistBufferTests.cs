using System.Collections.Generic;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ChatCatalogRowPersistBufferTests
    {
        [Fact]
        public void Enqueue_merges_same_jid_keeping_latest_row()
        {
            var buffer = new ChatCatalogRowPersistBuffer();
            var first = new ChatItem { JID = "1@g.us", Name = "a" };
            var second = new ChatItem { JID = "1@g.us", Name = "b" };

            Assert.Equal(1, buffer.Enqueue(new[] { first }));
            Assert.Equal(1, buffer.Enqueue(new[] { second }));

            IList<ChatItem> drained = buffer.TakeAll();
            Assert.Single(drained);
            Assert.Same(second, drained[0]);
        }

        [Fact]
        public void Enqueue_keeps_distinct_jids()
        {
            var buffer = new ChatCatalogRowPersistBuffer();
            buffer.Enqueue(new[]
            {
                new ChatItem { JID = "1@g.us" },
                new ChatItem { JID = "2@g.us" }
            });
            buffer.Enqueue(new[] { new ChatItem { JID = "3@lid" } });

            Assert.Equal(3, buffer.TakeAll().Count);
            Assert.Empty(buffer.TakeAll());
        }

        [Fact]
        public void Enqueue_skips_null_and_blank_jid()
        {
            var buffer = new ChatCatalogRowPersistBuffer();
            Assert.Equal(0, buffer.Enqueue(new ChatItem[] { null, new ChatItem { JID = "  " } }));
            Assert.Empty(buffer.TakeAll());
        }
    }
}
