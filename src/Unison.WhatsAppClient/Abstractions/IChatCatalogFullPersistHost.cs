// =============================================================================
// IChatCatalogFullPersistHost
//
// Side effects for ChatCatalogFullPersistDispatch (full catalogue + name maps,
// preview slice, and the suspend-tail critical snapshot).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IChatCatalogFullPersistHost
    {
        Task EnterPersistLockAsync();

        void ExitPersistLock();

        /// <summary>
        /// UI-thread snapshots of catalogue + contact/phone/alias maps, then durable writes.
        /// </summary>
        Task PersistFullCatalogAndNameMapsAsync();

        void LogFullPersistFailed(Exception error);

        /// <summary>
        /// Writes only the given list rows to <c>history_chat_preview</c> (mark-read path).
        /// </summary>
        Task PersistCatalogSliceAsync(IList<ChatItem> chats);

        void LogCatalogSliceSucceeded(int rowCount);

        void LogCatalogSliceFailed(Exception error);

        /// <summary>
        /// Compact suspend path: catalogue + alias maps only (no full contact rewrite).
        /// </summary>
        Task PersistCriticalSuspendStateAsync();

        void LogSuspendPersistFailed(Exception error);
    }
}
