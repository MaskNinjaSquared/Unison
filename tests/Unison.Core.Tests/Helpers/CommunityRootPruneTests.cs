using System.Collections.ObjectModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class CommunityRootPruneTests
    {
        private const string Parent = "120363111111111111@g.us";
        private const string ChildA = "120363222222222222@g.us";
        private const string ChildB = "120363333333333333@g.us";
        private const string Solo = "120363444444444444@g.us";

        [Fact]
        public void RemoveLinkedChildrenFromRoot_nests_under_existing_hub_without_touching_others()
        {
            var hub = new ChatItem { JID = Parent, Name = "Hub", IsCommunityParent = true };
            var a = new ChatItem { JID = ChildA, Name = "A", LinkedParentJid = Parent };
            var b = new ChatItem { JID = ChildB, Name = "B", LinkedParentJid = Parent };
            var solo = new ChatItem { JID = Solo, Name = "Solo" };
            var visible = new ObservableCollection<ChatItem> { hub, a, solo, b };

            int removed = CommunityRootPrune.RemoveLinkedChildrenFromRoot(
                visible,
                jid => jid,
                jid => null);

            Assert.Equal(2, removed);
            Assert.Equal(2, visible.Count);
            Assert.Contains(hub, visible);
            Assert.Contains(solo, visible);
            Assert.DoesNotContain(a, visible);
            Assert.DoesNotContain(b, visible);
            Assert.Equal(2, hub.GetCommunityGroupCount());
        }

        [Fact]
        public void RemoveLinkedChildrenFromRoot_inserts_resolved_hub_when_missing_from_visible()
        {
            var a = new ChatItem { JID = ChildA, Name = "A", LinkedParentJid = Parent };
            var solo = new ChatItem { JID = Solo, Name = "Solo" };
            var visible = new ObservableCollection<ChatItem> { a, solo };
            ChatItem synthetic = null;

            int removed = CommunityRootPrune.RemoveLinkedChildrenFromRoot(
                visible,
                jid => jid,
                jid =>
                {
                    synthetic = new ChatItem
                    {
                        JID = jid,
                        IsCommunityParent = true,
                        Kind = ChatKind.Community
                    };
                    return synthetic;
                });

            Assert.Equal(1, removed);
            Assert.Equal(2, visible.Count);
            Assert.Same(synthetic, visible[0]);
            Assert.Same(solo, visible[1]);
            Assert.Equal(1, synthetic.GetCommunityGroupCount());
        }

        [Fact]
        public void RemoveLinkedChildrenFromRoot_puts_children_back_when_hub_cannot_be_resolved()
        {
            var a = new ChatItem { JID = ChildA, Name = "A", LinkedParentJid = Parent };
            var visible = new ObservableCollection<ChatItem> { a };

            int removed = CommunityRootPrune.RemoveLinkedChildrenFromRoot(
                visible,
                jid => jid,
                jid => null);

            Assert.Equal(0, removed);
            Assert.Single(visible);
            Assert.Same(a, visible[0]);
        }
    }
}
