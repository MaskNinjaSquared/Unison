using System.Collections.Generic;
using Unison.Core.Helpers.History;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SessionOpenOnDemandPendingListTests
    {
        [Fact]
        public void Parse_empty_is_empty()
        {
            Assert.Empty(SessionOpenOnDemandPendingList.Parse(null));
            Assert.Empty(SessionOpenOnDemandPendingList.Parse(""));
            Assert.Empty(SessionOpenOnDemandPendingList.Parse("  "));
        }

        [Fact]
        public void Roundtrip_pipe_separated()
        {
            var list = new List<string> { "a@s.whatsapp.net", "b@lid" };
            string raw = SessionOpenOnDemandPendingList.Format(list);
            Assert.Equal("a@s.whatsapp.net|b@lid", raw);
            List<string> parsed = SessionOpenOnDemandPendingList.Parse(raw);
            Assert.Equal(2, parsed.Count);
            Assert.Equal("a@s.whatsapp.net", parsed[0]);
            Assert.Equal("b@lid", parsed[1]);
        }

        [Fact]
        public void TryAdd_is_mru_and_dedupes()
        {
            var list = new List<string> { "a@s.whatsapp.net", "b@lid" };
            Assert.True(SessionOpenOnDemandPendingList.TryAdd(list, "a@s.whatsapp.net", 12));
            Assert.Equal(new[] { "b@lid", "a@s.whatsapp.net" }, list);
            Assert.False(SessionOpenOnDemandPendingList.TryAdd(list, "a@s.whatsapp.net", 12));
        }

        [Fact]
        public void TryAdd_trims_oldest_when_over_cap()
        {
            var list = new List<string>();
            for (int i = 0; i < 5; i++)
            {
                Assert.True(SessionOpenOnDemandPendingList.TryAdd(list, i + "@s.whatsapp.net", 3));
            }

            Assert.Equal(3, list.Count);
            Assert.Equal("2@s.whatsapp.net", list[0]);
            Assert.Equal("4@s.whatsapp.net", list[2]);
        }

        [Fact]
        public void RemoveAny_is_case_insensitive()
        {
            var list = new List<string> { "A@s.whatsapp.net", "b@lid" };
            Assert.True(SessionOpenOnDemandPendingList.RemoveAny(
                list,
                new[] { "a@s.whatsapp.net" }));
            Assert.Single(list);
            Assert.Equal("b@lid", list[0]);
        }
    }
}
