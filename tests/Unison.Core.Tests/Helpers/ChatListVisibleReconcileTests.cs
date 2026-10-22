// =============================================================================
// Tests for ChatListVisibleReconcile.
// Budgeted Moves must leave the rest for a later turn; the desired order is
// applied in place so a cached source can continue without rebuilding.
// =============================================================================
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatListVisibleReconcileTests
    {
        [Fact]
        public void Already_aligned_rows_cost_no_mutations()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            var visible = new ObservableCollection<ChatItem> { a, b };
            var desired = new List<ChatItem> { a, b };

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 10);

            Assert.Equal(0, result.Mutations);
            Assert.False(result.Incomplete);
            Assert.Equal(new[] { a, b }, visible);
        }

        [Fact]
        public void Budget_stops_midway_and_marks_incomplete()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            ChatItem c = Row("c");
            ChatItem d = Row("d");
            // Visible is reverse of desired — every slot needs a Move/Insert.
            var visible = new ObservableCollection<ChatItem> { d, c, b, a };
            var desired = new List<ChatItem> { a, b, c, d };

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 2);

            Assert.Equal(2, result.Mutations);
            Assert.True(result.Incomplete);
            Assert.Equal(a, visible[0]);
            Assert.Equal(b, visible[1]);
        }

        [Fact]
        public void Second_pass_with_same_desired_list_finishes_without_rebuild()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            ChatItem c = Row("c");
            ChatItem d = Row("d");
            var visible = new ObservableCollection<ChatItem> { d, c, b, a };
            var desired = new List<ChatItem> { a, b, c, d };

            ChatListVisibleReconcileResult first = ChatListVisibleReconcile.Apply(visible, desired, 2);
            Assert.True(first.Incomplete);

            ChatListVisibleReconcileResult second = ChatListVisibleReconcile.Apply(visible, desired, 10);

            Assert.False(second.Incomplete);
            Assert.Equal(new[] { a, b, c, d }, visible);
        }

        [Fact]
        public void Trailing_rows_are_trimmed_within_budget()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            ChatItem orphan = Row("orphan");
            var visible = new ObservableCollection<ChatItem> { a, b, orphan };
            var desired = new List<ChatItem> { a, b };

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 10);

            Assert.False(result.Incomplete);
            Assert.Equal(1, result.Mutations);
            Assert.Equal(new[] { a, b }, visible);
        }

        private static ChatItem Row(string jid) => new ChatItem { JID = jid + "@s.whatsapp.net" };
    }
}
