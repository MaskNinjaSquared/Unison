// =============================================================================
// ChatListVisibleReconcile
//
// Presentation: applies a desired ChatItem order onto VisibleChats with a
// Move/Insert/Remove budget so one dispatcher turn cannot thrash the ListView.
// Stays in Core with other UI-collection helpers — not WhatsApp client policy.
// =============================================================================
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

            int mutations = 0;
            bool incomplete = false;

            for (int i = 0; i < desired.Count; i++)
            {
                ChatItem item = desired[i];
                if (i < visible.Count && ReferenceEquals(visible[i], item))
                {
                    continue;
                }

                if (mutations >= moveBudget)
                {
                    incomplete = true;
                    break;
                }

                int existingIndex = IndexOfReference(visible, item);
                if (existingIndex >= 0)
                {
                    visible.Move(existingIndex, i);
                }
                else
                {
                    visible.Insert(i, item);
                }

                mutations++;
            }

            if (!incomplete)
            {
                while (visible.Count > desired.Count)
                {
                    if (mutations >= moveBudget)
                    {
                        incomplete = true;
                        break;
                    }

                    visible.RemoveAt(visible.Count - 1);
                    mutations++;
                }
            }

            return new ChatListVisibleReconcileResult(mutations, incomplete);
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
