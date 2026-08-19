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



        /// <summary>Insert or replace. Returns the persisted snapshot.</summary>

        Task<ChatLocalState> UpsertAsync(

            string jid,

            ChatLocalStatus status,

            bool isWidgetPinned,

            bool isChatPinned,

            long? mutedUntil);



        /// <summary>

        /// Applies SQLite local fields onto a chat model.

        /// Does not overwrite <see cref="ChatItem.IsChatPinned"/> (history remains source of truth for now).

        /// </summary>

        void ApplyTo(ChatItem chat);



        Task ApplyToAsync(ChatItem chat);

    }

}

