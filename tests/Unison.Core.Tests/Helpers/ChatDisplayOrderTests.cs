// =============================================================================
// Characterization tests for ChatDisplayOrder.
//
// These lock in the order the chat list is in today. They are deliberately not
// aspirational: phase 3.9b moves the code that owns the ChatItem collection, and
// the point of these is to fail loudly if that move changes what the user sees.
// =============================================================================
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class ChatDisplayOrderTests
    {
        private static DateTime Utc(int day, int hour = 0) =>
            new DateTime(2026, 1, day, hour, 0, 0, DateTimeKind.Utc);

        private static ChatItem Chat(
            string name,
            DateTime? lastMessage = null,
            bool pinned = false,
            long? pinnedAt = null) =>
            new ChatItem
            {
                Name = name,
                LastMessageTimestampUtc = lastMessage,
                IsChatPinned = pinned,
                PinnedTimestamp = pinnedAt
            };

        private static bool LeftComesFirst(ChatItem left, ChatItem right) =>
            ChatDisplayOrder.Compare(left, right) < 0;

        // --- pinning ---------------------------------------------------------

        [Fact]
        public void Pinned_chat_comes_before_unpinned_even_when_older()
        {
            var pinnedOld = Chat("pinned", Utc(1), pinned: true, pinnedAt: 100);
            var unpinnedNew = Chat("unpinned", Utc(20));

            Assert.True(LeftComesFirst(pinnedOld, unpinnedNew));
            Assert.False(LeftComesFirst(unpinnedNew, pinnedOld));
        }

        [Fact]
        public void Among_pinned_chats_the_most_recently_pinned_comes_first()
        {
            var pinnedEarlier = Chat("a", Utc(20), pinned: true, pinnedAt: 100);
            var pinnedLater = Chat("b", Utc(1), pinned: true, pinnedAt: 200);

            Assert.True(LeftComesFirst(pinnedLater, pinnedEarlier));
        }

        [Fact]
        public void Pinned_chat_without_a_pin_timestamp_is_treated_as_pinned_at_zero()
        {
            var noPinTime = Chat("a", Utc(20), pinned: true, pinnedAt: null);
            var withPinTime = Chat("b", Utc(1), pinned: true, pinnedAt: 1);

            Assert.True(LeftComesFirst(withPinTime, noPinTime));
        }

        [Fact]
        public void Pinned_chats_with_the_same_pin_time_fall_through_to_last_message()
        {
            var older = Chat("a", Utc(1), pinned: true, pinnedAt: 100);
            var newer = Chat("b", Utc(20), pinned: true, pinnedAt: 100);

            Assert.True(LeftComesFirst(newer, older));
        }

        // --- recency ---------------------------------------------------------

        [Fact]
        public void More_recent_chat_comes_first()
        {
            var older = Chat("a", Utc(1));
            var newer = Chat("b", Utc(20));

            Assert.True(LeftComesFirst(newer, older));
        }

        [Fact]
        public void Chat_with_no_messages_sorts_after_a_chat_that_has_them()
        {
            var never = Chat("a", lastMessage: null);
            var some = Chat("b", Utc(1));

            Assert.True(LeftComesFirst(some, never));
        }

        [Fact]
        public void Unspecified_kind_is_read_as_utc_rather_than_local()
        {
            // Regression guard for the documented Brazil UTC-3 bug: treating an
            // Unspecified SQLite timestamp as local would shift it by +3h and
            // reorder the list. Same wall clock must compare equal.
            var fromSqlite = Chat("a", new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Unspecified));
            var fromSocket = Chat("b", new DateTime(2026, 1, 5, 12, 0, 0, DateTimeKind.Utc));

            Assert.Equal(
                string.Compare("a", "b", StringComparison.CurrentCultureIgnoreCase) < 0,
                LeftComesFirst(fromSqlite, fromSocket));
        }

        // --- tie-break -------------------------------------------------------

        [Fact]
        public void Equal_timestamps_fall_back_to_name_ignoring_case()
        {
            var alice = Chat("alice", Utc(1));
            var bob = Chat("Bob", Utc(1));

            Assert.True(LeftComesFirst(alice, bob));
        }

        [Fact]
        public void A_fresh_sync_with_no_messages_yet_has_a_stable_order()
        {
            // Name is the tie-break precisely so repeated passes do not reshuffle.
            var first = Chat("alpha");
            var second = Chat("beta");

            Assert.True(LeftComesFirst(first, second));
            Assert.False(LeftComesFirst(second, first));
        }

        // --- degenerate inputs -----------------------------------------------

        [Fact]
        public void Same_instance_compares_equal()
        {
            var chat = Chat("a", Utc(1));

            Assert.Equal(0, ChatDisplayOrder.Compare(chat, chat));
        }

        [Fact]
        public void Nulls_sort_to_the_end()
        {
            var chat = Chat("a", Utc(1));

            Assert.True(ChatDisplayOrder.Compare(null, chat) > 0);
            Assert.True(ChatDisplayOrder.Compare(chat, null) < 0);
        }

        // --- Reposition ------------------------------------------------------

        [Fact]
        public void Reposition_moves_a_freshly_bumped_chat_to_the_top()
        {
            var bumped = Chat("c", Utc(1));
            var chats = new ObservableCollection<ChatItem>
            {
                Chat("a", Utc(10)),
                Chat("b", Utc(5)),
                bumped
            };

            bumped.LastMessageTimestampUtc = Utc(20);
            ChatDisplayOrder.Reposition(chats, bumped);

            Assert.Same(bumped, chats[0]);
        }

        [Fact]
        public void Reposition_moves_a_stale_chat_down_to_its_new_place()
        {
            var stale = Chat("a", Utc(20));
            var chats = new ObservableCollection<ChatItem>
            {
                stale,
                Chat("b", Utc(10)),
                Chat("c", Utc(5))
            };

            stale.LastMessageTimestampUtc = Utc(7);
            ChatDisplayOrder.Reposition(chats, stale);

            Assert.Equal(new[] { "b", "a", "c" }, Names(chats));
        }

        [Fact]
        public void Reposition_ignores_a_chat_that_is_not_in_the_collection()
        {
            var chats = new ObservableCollection<ChatItem> { Chat("a", Utc(1)) };
            var stranger = Chat("z", Utc(28));

            ChatDisplayOrder.Reposition(chats, stranger);

            Assert.Single(chats);
            Assert.Equal(new[] { "a" }, Names(chats));
        }

        [Fact]
        public void Reposition_tolerates_nulls()
        {
            var chats = new ObservableCollection<ChatItem> { Chat("a", Utc(1)) };

            ChatDisplayOrder.Reposition(null, chats[0]);
            ChatDisplayOrder.Reposition(chats, null);

            Assert.Single(chats);
        }

        // --- SortInPlace -----------------------------------------------------

        [Fact]
        public void SortInPlace_orders_pinned_first_then_by_recency()
        {
            var chats = new ObservableCollection<ChatItem>
            {
                Chat("old", Utc(1)),
                Chat("new", Utc(20)),
                Chat("pinned", Utc(2), pinned: true, pinnedAt: 50)
            };

            ChatDisplayOrder.SortInPlace(chats);

            Assert.Equal(new[] { "pinned", "new", "old" }, Names(chats));
        }

        [Fact]
        public void SortInPlace_raises_no_notifications_when_the_list_is_already_ordered()
        {
            // The ListView reacts to every Move, so an already-sorted list must be
            // a no-op. This is the optimization in SortInPlace, pinned down.
            var chats = new ObservableCollection<ChatItem>
            {
                Chat("a", Utc(20)),
                Chat("b", Utc(10)),
                Chat("c", Utc(5))
            };

            int notifications = 0;
            ((INotifyCollectionChanged)chats).CollectionChanged += (s, e) => notifications++;

            ChatDisplayOrder.SortInPlace(chats);

            Assert.Equal(0, notifications);
        }

        [Fact]
        public void SortInPlace_ignores_lists_too_short_to_reorder()
        {
            var empty = new ObservableCollection<ChatItem>();
            var single = new ObservableCollection<ChatItem> { Chat("a") };

            ChatDisplayOrder.SortInPlace(null);
            ChatDisplayOrder.SortInPlace(empty);
            ChatDisplayOrder.SortInPlace(single);

            Assert.Empty(empty);
            Assert.Single(single);
        }

        private static string[] Names(ObservableCollection<ChatItem> chats)
        {
            var names = new string[chats.Count];
            for (int i = 0; i < chats.Count; i++)
            {
                names[i] = chats[i].Name;
            }

            return names;
        }
    }
}
