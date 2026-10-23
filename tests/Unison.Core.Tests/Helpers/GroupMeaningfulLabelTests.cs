using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class GroupMeaningfulLabelTests
    {
        [Fact]
        public void A_cached_meaningful_title_wins()
        {
            Assert.True(GroupMeaningfulLabel.Has(
                "g@g.us",
                "120363",
                jid => "Family",
                (label, jid, isGroup) => label == "Family"));
        }

        [Fact]
        public void Falls_back_to_the_row_name_when_the_cache_misses()
        {
            Assert.True(GroupMeaningfulLabel.Has(
                "g@g.us",
                "Family",
                jid => null,
                (label, jid, isGroup) => label == "Family"));
        }

        [Fact]
        public void Blank_everywhere_is_not_meaningful()
        {
            Assert.False(GroupMeaningfulLabel.Has(
                "g@g.us",
                "120363",
                null,
                (label, jid, isGroup) => false));
        }
    }
}
