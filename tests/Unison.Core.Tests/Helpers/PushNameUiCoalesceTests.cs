using Unison.Core.Helpers.Contacts;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class PushNameUiCoalesceTests
    {
        [Fact]
        public void Note_keeps_latest_name_per_jid()
        {
            var book = new PushNameUiCoalesce();
            book.Note("5511@s.whatsapp.net", "Ana");
            book.Note("5511@s.whatsapp.net", "Ana Clara");
            book.Note("5522@s.whatsapp.net", "Bob");

            var batch = book.Take();
            Assert.Equal(2, batch.Count);
            Assert.Contains(batch, p => p.Key == "5511@s.whatsapp.net" && p.Value == "Ana Clara");
            Assert.Contains(batch, p => p.Key == "5522@s.whatsapp.net" && p.Value == "Bob");
            Assert.Equal(0, book.Count);
        }
    }
}
