// =============================================================================
// ChatListVisibleReconcile
//
// Presentation: applies a desired ChatItem order onto VisibleChats with a
// Move/Insert/Remove budget so one dispatcher turn cannot thrash the ListView.
// Stays in Core with other UI-collection helpers — not WhatsApp client policy.
// =============================================================================
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public sealed class ChatListVisibleReconcileResult
    {
        public ChatListVisibleReconcileResult(int mutations, bool incomplete)
        {
            Mutations = mutations;
            Incomplete = incomplete;
        }

        public int Mutations { get; }

        public bool Incomplete { get; }
    }

    public static class ChatListVisibleReconcile
    {
        /// <summary>
        /// True when every visible slot already holds the desired row by reference — callers
        /// can skip Move/Insert work (and its ListView churn) after a no-op rebuild.
        /// </summary>
        public static bool IsAlreadyAligned(
            ObservableCollection<ChatItem> visible,
            IList<ChatItem> desired)
        {
            if (visible == null || desired == null)
            {
                return false;
            }

            if (visible.Count != desired.Count)
            {
                return false;
            }

            for (int i = 0; i < desired.Count; i++)
            {
                if (!ReferenceEquals(visible[i], desired[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public static ChatListVisibleReconcileResult Apply(
            ObservableCollection<ChatItem> visible,
            IList<ChatItem> desired,
            int moveBudget)
        {
            if (visible == null || desired == null)
            {
                return new ChatListVisibleReconcileResult(0, false);
            }

            if (moveBudget < 0)
            {
                moveBudget = 0;
            }

            if (IsAlreadyAligned(visible, desired))
            {
                return new ChatListVisibleReconcileResult(0, false);
            }

            var desiredIndex = new Dictionary<ChatItem, int>(desired.Count, ReferenceComparer.Instance);
            for (int i = 0; i < desired.Count; i++)
            {
                if (desired[i] != null && !desiredIndex.ContainsKey(desired[i]))
                {
                    desiredIndex[desired[i]] = i;
                }
            }

            // Stale rows go first: left in place they shift every later index and a
            // top-down walk would spend one Move per row below them.
            int mutations = RemoveStaleRows(visible, desiredIndex, moveBudget);
            if (mutations >= moveBudget && !IsAlreadyAligned(visible, desired))
            {
                return new ChatListVisibleReconcileResult(mutations, true);
            }

            // Rows on the longest already-ordered run stay put; only the rest move.
            HashSet<ChatItem> settled = LongestOrderedRun(visible, desiredIndex);
            for (int j = 0; j < desired.Count; j++)
            {
                ChatItem item = desired[j];
                if (item == null || settled.Contains(item))
                {
                    continue;
                }

                if (mutations >= moveBudget)
                {
                    return new ChatListVisibleReconcileResult(mutations, true);
                }

                int predecessorIndex = j == 0 ? -1 : IndexOfReference(visible, desired[j - 1]);
                int currentIndex = IndexOfReference(visible, item);
                if (currentIndex < 0)
                {
                    visible.Insert(predecessorIndex + 1, item);
                    mutations++;
                }
                else
                {
                    int target = currentIndex < predecessorIndex ? predecessorIndex : predecessorIndex + 1;
                    if (target != currentIndex)
                    {
                        visible.Move(currentIndex, target);
                        mutations++;
                    }
                }

                settled.Add(item);
            }

            return new ChatListVisibleReconcileResult(mutations, false);
        }

        private static int RemoveStaleRows(
            ObservableCollection<ChatItem> visible,
            Dictionary<ChatItem, int> desiredIndex,
            int moveBudget)
        {
            var seen = new HashSet<ChatItem>(ReferenceComparer.Instance);
            var staleIndexes = new List<int>();
            for (int i = 0; i < visible.Count; i++)
            {
                ChatItem row = visible[i];
                if (row == null || !desiredIndex.ContainsKey(row) || !seen.Add(row))
                {
                    staleIndexes.Add(i);
                }
            }

            int removed = 0;
            for (int k = staleIndexes.Count - 1; k >= 0 && removed < moveBudget; k--)
            {
                visible.RemoveAt(staleIndexes[k]);
                removed++;
            }

            return removed;
        }

        private static HashSet<ChatItem> LongestOrderedRun(
            ObservableCollection<ChatItem> visible,
            Dictionary<ChatItem, int> desiredIndex)
        {
            int count = visible.Count;
            var tailPositions = new List<int>(count);
            var previous = new int[count];
            int bestEnd = -1;
            int bestLength = 0;
            for (int i = 0; i < count; i++)
            {
                int value = desiredIndex[visible[i]];
                int lo = 0;
                int hi = tailPositions.Count;
                while (lo < hi)
                {
                    int mid = (lo + hi) / 2;
                    if (desiredIndex[visible[tailPositions[mid]]] < value)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid;
                    }
                }

                previous[i] = lo > 0 ? tailPositions[lo - 1] : -1;
                if (lo == tailPositions.Count)
                {
                    tailPositions.Add(i);
                }
                else
                {
                    tailPositions[lo] = i;
                }

                // Ties keep the run that ends nearest the top so on-screen rows stay put.
                if (lo + 1 > bestLength)
                {
                    bestLength = lo + 1;
                    bestEnd = i;
                }
            }

            var run = new HashSet<ChatItem>(ReferenceComparer.Instance);
            int cursor = bestEnd;
            while (cursor >= 0)
            {
                run.Add(visible[cursor]);
                cursor = previous[cursor];
            }

            return run;
        }

        private sealed class ReferenceComparer : IEqualityComparer<ChatItem>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public bool Equals(ChatItem x, ChatItem y) => ReferenceEquals(x, y);

            public int GetHashCode(ChatItem obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static int IndexOfReference(ObservableCollection<ChatItem> visible, ChatItem item)
        {
            for (int i = 0; i < visible.Count; i++)
            {
                if (ReferenceEquals(visible[i], item))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
