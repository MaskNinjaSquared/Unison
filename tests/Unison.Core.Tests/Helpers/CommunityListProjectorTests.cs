using System.Collections.Generic;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.Core.State;
using Xunit;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityListProjectorTests
    {
        private const string Parent = "120363111111111111@g.us";
        private const string ChildA = "120363222222222222@g.us";
        private const string ChildB = "120363333333333333@g.us";
        private const string Other = "120363444444444444@g.us";

        [Fact]
        public void Project_nests_linked_children_under_parent_hub()
        {
            var parent = new ChatItem { JID = Parent, Name = "Hub", IsCommunityParent = true };
            var a = new ChatItem { JID = ChildA, Name = "A", LinkedParentJid = Parent, Kind = ChatKind.Group };
            var b = new ChatItem { JID = ChildB, Name = "B", LinkedParentJid = Parent, Kind = ChatKind.Group };
            var other = new ChatItem { JID = Other, Name = "Solo", Kind = ChatKind.Group };

            var catalog = new Dictionary<string, ChatItem>
            {
                [Parent] = parent,
                [ChildA] = a,
                [ChildB] = b,
                [Other] = other
            };

            List<ChatItem> projected = CommunityListProjector.Project(
                new[] { parent, a, b, other },
                jid => catalog.TryGetValue(jid, out ChatItem c) ? c : null,
                jid => null,
                jid => jid);

            Assert.Equal(2, projected.Count);
            Assert.Contains(parent, projected);
            Assert.Contains(other, projected);
            Assert.Equal(2, parent.GetCommunityGroupCount());
            Assert.Equal("Community · 2 groups", parent.GetCommunitySubtitle(null));
            Assert.DoesNotContain(a, projected);
            Assert.DoesNotContain(b, projected);
        }

        [Fact]
        public void Project_creates_synthetic_parent_when_catalog_missing()
        {
            var a = new ChatItem { JID = ChildA, Name = "A", LinkedParentJid = Parent, Kind = ChatKind.Group };
            ChatItem synthetic = null;

            List<ChatItem> projected = CommunityListProjector.Project(
                new[] { a },
                jid => null,
                jid =>
                {
                    synthetic = new ChatItem { JID = jid, Id = jid, IsCommunityParent = true };
                    return synthetic;
                },
                jid => jid);

            Assert.Single(projected);
            Assert.Same(synthetic, projected[0]);
            Assert.Equal(ChatKind.Community, synthetic.Kind);
            Assert.Equal(1, synthetic.GetCommunityGroupCount());
            Assert.Same(a, synthetic.GetCommunityGroups()[0]);
        }

        [Fact]
        public void OrderMembers_pins_announcements_first()
        {
            var avisos = new ChatItem
            {
                JID = ChildA,
                Name = "Avisos",
                IsCommunityAnnounce = true,
                LastMessageTimestampUtc = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
            };
            var geral = new ChatItem
            {
                JID = ChildB,
                Name = "Geral",
                LastMessageTimestampUtc = new System.DateTime(2026, 10, 21, 0, 0, 0, System.DateTimeKind.Utc)
            };

            var ordered = CommunityHubPresentation.OrderMembers(new[] { geral, avisos });
            Assert.Equal(2, ordered.Count);
            Assert.Same(avisos, ordered[0]);
            Assert.Same(geral, ordered[1]);
        }

        [Fact]
        public void Project_pins_announcements_first_under_hub()
        {
            var parent = new ChatItem { JID = Parent, Name = "Hub", IsCommunityParent = true };
            var geral = new ChatItem
            {
                JID = ChildB,
                Name = "Geral",
                LinkedParentJid = Parent,
                Kind = ChatKind.Group,
                LastMessageTimestampUtc = new System.DateTime(2026, 10, 21, 0, 0, 0, System.DateTimeKind.Utc)
            };
            var avisos = new ChatItem
            {
                JID = ChildA,
                Name = "Avisos",
                LinkedParentJid = Parent,
                Kind = ChatKind.Group,
                IsCommunityAnnounce = true,
                LastMessageTimestampUtc = new System.DateTime(2026, 1, 1, 0, 0, 0, System.DateTimeKind.Utc)
            };

            CommunityListProjector.Project(
                new[] { parent, geral, avisos },
                jid => jid == Parent ? parent : null,
                jid => null,
                jid => jid);

            var members = parent.GetCommunityGroups();
            Assert.Equal(2, members.Count);
            Assert.Same(avisos, members[0]);
            Assert.Same(geral, members[1]);
        }

        [Fact]
        public void TryGetDetailHeader_prefers_stamped_community_title()
        {
            var child = new ChatItem
            {
                JID = ChildA,
                Name = "Geral",
                LinkedParentJid = Parent,
                LinkedParentDisplayName = "Minha Comunidade",
                Kind = ChatKind.Group
            };

            bool ok = CommunityHubPresentation.TryGetDetailHeader(
                child,
                jid => null,
                null,
                out string title,
                out string subtitle);

            Assert.True(ok);
            Assert.Equal("Minha Comunidade", title);
            Assert.Equal("Geral", subtitle);
        }

        [Fact]
        public void ResolveHubTitle_uses_cached_group_subject_before_community_fallback()
        {
            var subjects = new DisplayNameTable();
            subjects.Set(Parent, "Minha Comunidade");
            GroupNaming naming = BuildGroupNaming(subjects);

            var hub = new ChatItem
            {
                JID = Parent,
                IsCommunityParent = true
            };

            string title = CommunityHubPresentation.ResolveHubTitle(
                hub,
                jid => naming.ResolveChatDisplayName(jid),
                new FallbackStrings());

            Assert.Equal("Minha Comunidade", title);
        }

        [Fact]
        public void TryStampHubLabel_stamps_subject_onto_hub_and_members()
        {
            var subjects = new DisplayNameTable();
            subjects.Set(Parent, "Minha Comunidade");
            GroupNaming naming = BuildGroupNaming(subjects);

            var hub = new ChatItem { JID = Parent, IsCommunityParent = true };
            var child = new ChatItem { JID = ChildA, Name = "Geral", LinkedParentJid = Parent };
            hub.SetCommunityGroups(new[] { child });

            Assert.True(CommunityHubPresentation.TryStampHubLabel(
                hub,
                jid => naming.ResolveChatDisplayName(jid)));

            Assert.Equal("Minha Comunidade", hub.Name);
            Assert.Equal("Minha Comunidade", child.LinkedParentDisplayName);
        }

        [Fact]
        public void TryStampHubLabel_returns_false_when_only_community_fallback_would_apply()
        {
            GroupNaming naming = BuildGroupNaming(new DisplayNameTable());
            var hub = new ChatItem { JID = Parent, IsCommunityParent = true };

            Assert.False(CommunityHubPresentation.TryStampHubLabel(
                hub,
                jid => naming.ResolveChatDisplayName(jid)));
            Assert.True(string.IsNullOrWhiteSpace(hub.Name));
        }

        private static GroupNaming BuildGroupNaming(DisplayNameTable subjects)
        {
            var aliases = new JidAliasTable();
            aliases.BindSelf(() => "5511911112222@s.whatsapp.net", () => null);
            var markers = new HubMarkers();
            var names = new ContactDisplayNames(
                aliases,
                new DisplayNameTable(),
                new DisplayNameTable(),
                new RecordingPersonStore(),
                markers);
            names.Bind(() => new SelfNameInputs { Id = "5511911112222@s.whatsapp.net" }, null);
            return new GroupNaming(
                names,
                new HubJids(aliases),
                markers,
                new FallbackStrings(),
                subjects);
        }

        private sealed class FallbackStrings : IStringResources
        {
            public string Get(string key, string fallback = null) =>
                key == "Chat_CommunityFallbackName" ? "Comunidade" : fallback ?? key;
        }

        private sealed class HubMarkers : ISelfMarkerNaming
        {
            public bool IsMarkerLabel(string label) => false;

            public string StripMarker(string label) => label;
        }

        private sealed class HubJids : IJidResolver
        {
            private readonly JidAliasTable _aliases;

            public HubJids(JidAliasTable aliases) => _aliases = aliases;

            public Profile Self => null;

            public string GetCanonicalJid(string jid) => _aliases.GetCanonicalJid(jid);

            public bool TryGetAlias(string jid, out string alias)
            {
                alias = null;
                return false;
            }

            public bool IsSelfLinked(string jid) => _aliases.IsSelfLinked(jid);
        }

        [Fact]
        public void ApplyMemberAvatarFallback_copies_announce_avatar_onto_hub()
        {
            var hub = new ChatItem { JID = Parent, IsCommunityParent = true };
            var avisos = new ChatItem
            {
                JID = ChildA,
                IsCommunityAnnounce = true,
                AvatarUrl = "ms-appdata:///local/avisos.jpg"
            };
            var geral = new ChatItem
            {
                JID = ChildB,
                AvatarUrl = "ms-appdata:///local/geral.jpg"
            };
            hub.SetCommunityGroups(new[] { geral, avisos });

            CommunityHubPresentation.ApplyMemberAvatarFallback(hub);

            Assert.Equal("ms-appdata:///local/avisos.jpg", hub.AvatarUrl);
        }

        [Fact]
        public void Project_hub_tip_uses_group_name_chevron_and_sender_detail()
        {
            var parent = new ChatItem { JID = Parent, Name = "Hub", IsCommunityParent = true };
            var newer = new ChatItem
            {
                JID = ChildA,
                Name = "Geral",
                LinkedParentJid = Parent,
                Kind = ChatKind.Group,
                LastMessage = "ola",
                LastMessageAuthor = "Alice: ",
                LastMessageTimestampUtc = new System.DateTime(2026, 10, 21, 12, 0, 0, System.DateTimeKind.Utc)
            };
            var older = new ChatItem
            {
                JID = ChildB,
                Name = "Avisos",
                LinkedParentJid = Parent,
                Kind = ChatKind.Group,
                LastMessage = "old",
                LastMessageAuthor = "Bob: ",
                LastMessageTimestampUtc = new System.DateTime(2026, 10, 20, 12, 0, 0, System.DateTimeKind.Utc)
            };

            CommunityListProjector.Project(
                new[] { parent, newer, older },
                jid => jid == Parent ? parent : null,
                jid => null,
                jid => jid);

            Assert.Equal("Geral", parent.LastMessageAuthor);
            Assert.True(parent.LastPreview.ShowAuthorChevron);
            Assert.Equal("Alice: ", parent.LastPreview.AuthorDetail);
            Assert.Equal("ola", parent.LastMessage);
        }

        [Fact]
        public void Project_hub_tip_localizes_announcements_group_label()
        {
            var parent = new ChatItem { JID = Parent, Name = "Hub", IsCommunityParent = true };
            var avisos = new ChatItem
            {
                JID = ChildA,
                Name = "Announcements",
                LinkedParentJid = Parent,
                Kind = ChatKind.Group,
                IsCommunityAnnounce = true,
                LastMessage = "novo aviso",
                LastMessageAuthor = "Admin: ",
                LastMessageTimestampUtc = new System.DateTime(2026, 10, 21, 12, 0, 0, System.DateTimeKind.Utc)
            };

            CommunityListProjector.Project(
                new[] { parent, avisos },
                jid => jid == Parent ? parent : null,
                jid => null,
                jid => jid,
                new AnnounceStrings("Avisos"));

            Assert.Equal("Avisos", parent.LastMessageAuthor);
            Assert.True(parent.LastPreview.ShowAuthorChevron);
            Assert.Equal("Admin: ", parent.LastPreview.AuthorDetail);
        }

        private sealed class AnnounceStrings : Unison.Core.Contracts.IStringResources
        {
            private readonly string _avisos;

            public AnnounceStrings(string avisos)
            {
                _avisos = avisos;
            }

            public string Get(string key, string fallback = null)
            {
                if (key == "Chat_CommunityAnnouncements")
                {
                    return _avisos;
                }

                return fallback ?? key;
            }
        }
    }
}
