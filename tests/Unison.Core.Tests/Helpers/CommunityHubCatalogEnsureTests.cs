// =============================================================================
// CommunityHubCatalogEnsure — materialize / refresh community parent catalogue rows.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityHubCatalogEnsureTests
    {
        private const string Hub = "120363999999999999@g.us";

        [Fact]
        public void Creates_hub_when_missing_and_subject_is_meaningful()
        {
            var rows = new Dictionary<string, ChatItem>(StringComparer.OrdinalIgnoreCase);
            ChatItem added = null;

            bool changed = CommunityHubCatalogEnsure.TryEnsure(
                Hub,
                "Truckpag HQ",
                findRow: jid =>
                {
                    ChatItem row;
                    return rows.TryGetValue(jid, out row) ? row : null;
                },
                addRow: row =>
                {
                    added = row;
                    rows[row.JID] = row;
                },
                isMeaningfulLabel: (label, jid) =>
                    MeaningfulChatLabel.IsMeaningful(label, jid, isGroup: true));

            Assert.True(changed);
            Assert.NotNull(added);
            Assert.True(added.IsCommunityParent);
            Assert.Equal(ChatKind.Community, added.Kind);
            Assert.Equal("Truckpag HQ", added.Name);
            Assert.Equal(Hub, added.JID);
        }

        [Fact]
        public void Updates_name_on_existing_hub()
        {
            var hub = new ChatItem
            {
                JID = Hub,
                Name = "120363999999999999",
                IsCommunityParent = true
            };

            bool changed = CommunityHubCatalogEnsure.TryEnsure(
                Hub,
                "Truckpag HQ",
                findRow: _ => hub,
                addRow: _ => throw new InvalidOperationException("must not add"),
                isMeaningfulLabel: (label, jid) =>
                    MeaningfulChatLabel.IsMeaningful(label, jid, isGroup: true));

            Assert.True(changed);
            Assert.Equal("Truckpag HQ", hub.Name);
        }

        [Fact]
        public void Marks_existing_row_as_community_parent()
        {
            var row = new ChatItem { JID = Hub, Name = "Truckpag HQ", IsGroup = true };

            bool changed = CommunityHubCatalogEnsure.TryEnsure(
                Hub,
                "Truckpag HQ",
                findRow: _ => row,
                addRow: _ => throw new InvalidOperationException("must not add"),
                isMeaningfulLabel: (label, jid) =>
                    MeaningfulChatLabel.IsMeaningful(label, jid, isGroup: true));

            Assert.True(changed);
            Assert.True(row.IsCommunityParent);
            Assert.Equal(ChatKind.Community, row.Kind);
        }

        [Fact]
        public void Does_not_overwrite_usable_name_with_blacklisted_subject()
        {
            var hub = new ChatItem
            {
                JID = Hub,
                Name = "Truckpag HQ",
                IsCommunityParent = true
            };

            bool changed = CommunityHubCatalogEnsure.TryEnsure(
                Hub,
                "invite link group",
                findRow: _ => hub,
                addRow: _ => throw new InvalidOperationException("must not add"),
                isMeaningfulLabel: (label, jid) =>
                    MeaningfulChatLabel.IsMeaningful(label, jid, isGroup: true));

            Assert.False(changed);
            Assert.Equal("Truckpag HQ", hub.Name);
        }

        [Fact]
        public void Refuses_to_create_without_meaningful_subject()
        {
            int adds = 0;

            bool changed = CommunityHubCatalogEnsure.TryEnsure(
                Hub,
                "120363999999999999",
                findRow: _ => null,
                addRow: _ => adds++,
                isMeaningfulLabel: (label, jid) =>
                    MeaningfulChatLabel.IsMeaningful(label, jid, isGroup: true));

            Assert.False(changed);
            Assert.Equal(0, adds);
        }
    }
}
