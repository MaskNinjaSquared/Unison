using System.Collections.Generic;
using Unison.Core.Helpers.Chats;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityHubLabelPassTests
    {
        [Fact]
        public void StampUnresolved_stamps_placeholder_hubs_once_and_skips_resolved()
        {
            var resolved = new ChatItem
            {
                JID = "120363aaa@g.us",
                IsCommunityParent = true,
                Name = "Já resolvida"
            };
            var placeholder = new ChatItem
            {
                JID = "120363bbb@g.us",
                IsCommunityParent = true
            };
            var child = new ChatItem
            {
                JID = "120363ccc@g.us",
                Name = "Geral",
                LinkedParentJid = placeholder.JID
            };
            placeholder.SetCommunityGroups(new[] { child });

            int changed = CommunityHubLabelPass.StampUnresolved(
                new[] { resolved, placeholder },
                jid => jid == placeholder.JID ? "Nova Comunidade" : "Ignorado");

            Assert.Equal(1, changed);
            Assert.Equal("Já resolvida", resolved.Name);
            Assert.Equal("Nova Comunidade", placeholder.Name);
            Assert.Equal("Nova Comunidade", child.LinkedParentDisplayName);
        }

        [Fact]
        public void StampUnresolved_returns_zero_when_subjects_still_missing()
        {
            var hub = new ChatItem
            {
                JID = "120363ddd@g.us",
                IsCommunityParent = true
            };

            int changed = CommunityHubLabelPass.StampUnresolved(
                new[] { hub },
                jid => "Community");

            Assert.Equal(0, changed);
            Assert.True(string.IsNullOrWhiteSpace(hub.Name));
        }

        [Fact]
        public void StampUnresolved_ignores_non_community_rows()
        {
            var group = new ChatItem { JID = "120363eee@g.us", IsGroup = true };

            int changed = CommunityHubLabelPass.StampUnresolved(
                new[] { group },
                jid => "Grupo X");

            Assert.Equal(0, changed);
            Assert.True(string.IsNullOrWhiteSpace(group.Name));
        }
    }
}
