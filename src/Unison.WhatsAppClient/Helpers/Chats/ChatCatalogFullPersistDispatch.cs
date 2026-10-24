// =============================================================================
// ChatCatalogFullPersistDispatch
//
// PersistDataAsync / PersistChatCatalogSliceAsync / PersistSuspendTailAsync
// orchestration. Lock, SQLite writes and UI snapshots stay on the host.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class ChatCatalogFullPersistDispatch
    {
        public static async Task<bool> RunFullAsync(IChatCatalogFullPersistHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            await host.EnterPersistLockAsync().ConfigureAwait(false);
            try
            {
                await host.PersistFullCatalogAndNameMapsAsync().ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                host.LogFullPersistFailed(ex);
                return false;
            }
            finally
            {
                host.ExitPersistLock();
            }
        }

        public static async Task RunSliceAsync(
            IChatCatalogFullPersistHost host,
            IList<ChatItem> chats)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (chats == null || chats.Count == 0)
            {
                return;
            }

            await host.EnterPersistLockAsync().ConfigureAwait(false);
            try
            {
                await host.PersistCatalogSliceAsync(chats).ConfigureAwait(false);
                host.LogCatalogSliceSucceeded(chats.Count);
            }
            catch (Exception ex)
            {
                host.LogCatalogSliceFailed(ex);
            }
            finally
            {
                host.ExitPersistLock();
            }
        }

        public static async Task RunSuspendTailAsync(IChatCatalogFullPersistHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                // Recent incoming messages are already durable in the append-only journal.
                // Only the compact chat-list/alias snapshot is best effort under suspend.
                await host.PersistCriticalSuspendStateAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                host.LogSuspendPersistFailed(ex);
            }
        }
    }
}
