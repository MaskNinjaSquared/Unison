// =============================================================================
// Tests for SharePickerCatalog — Share Target picker ordering + rebuild gate.
// =============================================================================
using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class SharePickerCatalogTests
    {
        [Fact]
        public void BuildOrdered_null_input_returns_empty()
        {
            IReadOnlyList<ChatItem> result = SharePickerCatalog.BuildOrdered(null, null);
            Assert.Empty(result);
        }

        [Fact]
        public void BuildOrdered_skips_null_rows_and_sorts_by_name()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "b@s.whatsapp.net", Name = "Bob" },
                null,
                new ChatItem { JID = "a@s.whatsapp.net", Name = "Alice" },
            };

            IReadOnlyList<ChatItem> result = SharePickerCatalog.BuildOrdered(chats, c => c.Name);

            Assert.Equal(2, result.Count);
            Assert.Equal("Alice", result[0].Name);
            Assert.Equal("Bob", result[1].Name);
        }

        [Fact]
        public void BuildOrdered_falls_back_to_jid_when_name_blank()
        {
            var chats = new List<ChatItem>
            {
                new ChatItem { JID = "z@s.whatsapp.net", Name = " " },
                new ChatItem { JID = "a@s.whatsapp.net", Name = "Alice" },
            };

            IReadOnlyList<ChatItem> result = SharePickerCatalog.BuildOrdered(chats, c => c.Name);

            Assert.Equal("Alice", result[0].Name);
            Assert.Equal("z@s.whatsapp.net", result[1].JID);
        }

        [Fact]
        public void ShouldRebuildWhileOpen_only_when_picker_open_and_catalog_changed()
        {
            Assert.False(SharePickerCatalog.ShouldRebuildWhileOpen(false, true));
            Assert.False(SharePickerCatalog.ShouldRebuildWhileOpen(true, false));
            Assert.True(SharePickerCatalog.ShouldRebuildWhileOpen(true, true));
        }

        [Fact]
        public void SharePickerCatalogRefresh_mirrors_rebuild_gate_and_force_on_ensure()
        {
            Assert.True(SharePickerCatalogRefresh.ShouldRebuild(true, true));
            Assert.False(SharePickerCatalogRefresh.ShouldRebuild(false, true));
            Assert.True(SharePickerCatalogRefresh.ShouldForceRebuildOnEnsure(true));
            Assert.False(SharePickerCatalogRefresh.ShouldForceRebuildOnEnsure(false));
        }
    }
}
