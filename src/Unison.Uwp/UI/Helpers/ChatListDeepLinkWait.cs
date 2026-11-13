// =============================================================================
// ChatListDeepLinkWait
//
// Waits for a JID to appear in the chat list without a Task.Delay poll loop.
// One CollectionChanged subscription + a single timeout Task.Delay.
// =============================================================================
using System;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.Core.ViewModels;

namespace Unison.Uwp.UI.Helpers
{
    internal static class ChatListDeepLinkWait
    {
        public static async Task<ChatItem> WaitForChatAsync(
            Func<string, ChatItem> findByJid,
            ChatListViewModel listVm,
            string jid,
            TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(jid) || findByJid == null)
            {
                return null;
            }

            ChatItem hit = findByJid(jid);
            if (hit != null)
            {
                return hit;
            }

            if (listVm?.VisibleChats == null)
            {
                return null;
            }

            var tcs = new TaskCompletionSource<ChatItem>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            NotifyCollectionChangedEventHandler onChanged = (s, e) =>
            {
                ChatItem found = findByJid(jid);
                if (found != null)
                {
                    tcs.TrySetResult(found);
                }
            };

            listVm.VisibleChats.CollectionChanged += onChanged;
            try
            {
                hit = findByJid(jid);
                if (hit != null)
                {
                    return hit;
                }

                Task winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout)).ConfigureAwait(true);
                if (winner == tcs.Task)
                {
                    return await tcs.Task.ConfigureAwait(true);
                }

                return findByJid(jid);
            }
            finally
            {
                listVm.VisibleChats.CollectionChanged -= onChanged;
            }
        }
    }
}
