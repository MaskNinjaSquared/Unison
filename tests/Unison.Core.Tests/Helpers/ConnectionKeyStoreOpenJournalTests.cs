using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ConnectionKeyStoreOpenJournalTests
    {
        [Fact]
        public void EventName_distinguishes_fast_resume()
        {
            Assert.Equal("fast-resume-key-store-reused", ConnectionKeyStoreOpenJournal.EventName(true));
            Assert.Equal("key-store-cold-loaded", ConnectionKeyStoreOpenJournal.EventName(false));
        }

        [Fact]
        public void Detail_formats_counts()
        {
            Assert.Equal("sessions=3; prekeys=12", ConnectionKeyStoreOpenJournal.Detail(3, 12));
        }
    }
}
