// =============================================================================
// Tests for ChatListVisibleReconcile.
// Budgeted Moves must leave the rest for a later turn; the desired order is
// applied in place so a cached source can continue without rebuilding.
// =============================================================================
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Unison.Core.Helpers;
using Unison.Core.Models;
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

            Assert.True(ChatListVisibleReconcile.IsAlreadyAligned(visible, desired));

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 10);

            Assert.Equal(0, result.Mutations);
            Assert.False(result.Incomplete);
            Assert.Equal(new[] { a, b }, visible);
        }

        [Fact]
        public void IsAlreadyAligned_false_when_order_differs()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            var visible = new ObservableCollection<ChatItem> { b, a };
            var desired = new List<ChatItem> { a, b };

            Assert.False(ChatListVisibleReconcile.IsAlreadyAligned(visible, desired));
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

        [Fact]
        public void Stale_rows_at_the_top_are_removed_without_walking_the_list()
        {
            var desired = new List<ChatItem>();
            for (int i = 0; i < 424; i++)
            {
                desired.Add(Row("c" + i));
            }

            var visible = new ObservableCollection<ChatItem>();
            for (int i = 0; i < 12; i++)
            {
                visible.Add(Row("stale" + i));
            }

            foreach (ChatItem row in desired)
            {
                visible.Add(row);
            }

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 12);

            Assert.False(result.Incomplete);
            Assert.Equal(12, result.Mutations);
            Assert.Equal(desired, visible);
        }

        [Fact]
        public void Single_row_jumping_to_the_bottom_costs_one_move()
        {
            var desired = new List<ChatItem>();
            for (int i = 0; i < 100; i++)
            {
                desired.Add(Row("c" + i));
            }

            var visible = new ObservableCollection<ChatItem>();
            visible.Add(desired[99]);
            for (int i = 0; i < 99; i++)
            {
                visible.Add(desired[i]);
            }

            ChatListVisibleReconcileResult result = ChatListVisibleReconcile.Apply(visible, desired, 10);

            Assert.False(result.Incomplete);
            Assert.Equal(1, result.Mutations);
            Assert.Equal(desired, visible);
        }

        [Fact]
        public void Mixed_stale_new_and_moved_rows_converge_to_desired_order()
        {
            ChatItem a = Row("a");
            ChatItem b = Row("b");
            ChatItem c = Row("c");
            ChatItem d = Row("d");
            ChatItem e = Row("e");
            ChatItem stale = Row("stale");
            ChatItem fresh = Row("fresh");
            var visible = new ObservableCollection<ChatItem> { stale, c, a, e, b, d };
            var desired = new List<ChatItem> { fresh, a, b, c, d, e };

            int passes = 0;
            ChatListVisibleReconcileResult result;
            do
            {
                result = ChatListVisibleReconcile.Apply(visible, desired, 2);
                passes++;
            }
            while (result.Incomplete && passes < 10);

            Assert.False(result.Incomplete);
            Assert.Equal(desired, visible);
        }

        private static ChatItem Row(string jid) => new ChatItem { JID = jid + "@s.whatsapp.net" };
    }
}
