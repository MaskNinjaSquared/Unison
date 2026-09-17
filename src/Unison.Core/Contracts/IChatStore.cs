using System.Threading.Tasks;

using Unison.Core.Models;



namespace Unison.Core.Contracts

{

    /// <summary>

    /// SQLite store for local chat metadata (same DB as Person).

    /// </summary>

    public interface IChatStore

    {

        Task InitializeAsync();



        /// <summary>Load every row into the in-memory cache.</summary>

        Task WarmAsync();



        ChatLocalState TryGetCached(string jid);



        Task<ChatLocalState> GetAsync(string jid);



        // Each write names one field. Writing the whole row instead means every caller has to be
        // holding a current copy of the other three, and the ones that were not silently reverted
        // them: muting a chat from the info panel used to store IsChatPinned=false over a pin.

        /// <summary>The chat-list pin, mirrored from pin_v1 / app-state.</summary>
        Task SetChatPinnedAsync(string jid, bool pinned);

        /// <summary>The Start-screen tile.</summary>
        Task SetWidgetPinnedAsync(string jid, bool pinned);

        /// <summary>Unix seconds mute deadline; null unmutes.</summary>
        Task SetMutedUntilAsync(string jid, long? mutedUntil);

        Task SetStatusAsync(string jid, ChatStatus status);



        /// <summary>

        /// Applies SQLite local fields onto a chat model (widget pin, chat-list pin, mute).

        /// </summary>

        void ApplyTo(ChatItem chat);



        Task ApplyToAsync(ChatItem chat);

    }

}

