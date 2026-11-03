using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class StatusStartIndexTests
    {
        private static readonly IReadOnlyList<HistoryStatus> Items = new[]
        {
            new HistoryStatus { MessageId = "A1" },
            new HistoryStatus { MessageId = "B2" },
            new HistoryStatus { MessageId = "C3" }
        };

        [Fact]
        public void Tapped_status_in_the_middle_starts_there()
        {
            Assert.Equal(1, StatusStartIndex.Resolve(Items, "B2"));
        }

        [Fact]
        public void Unknown_id_starts_at_the_first_status()
        {
            Assert.Equal(0, StatusStartIndex.Resolve(Items, "ZZ"));
        }

        [Fact]
        public void No_id_starts_at_the_first_status()
        {
            Assert.Equal(0, StatusStartIndex.Resolve(Items, null));
        }

        [Fact]
        public void Id_match_ignores_case()
        {
            Assert.Equal(2, StatusStartIndex.Resolve(Items, "c3"));
        }

        [Fact]
        public void Empty_items_start_at_zero()
        {
            Assert.Equal(0, StatusStartIndex.Resolve(new HistoryStatus[0], "A1"));
        }
    }
}
