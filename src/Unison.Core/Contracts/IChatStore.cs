using System.Collections.Generic;
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

        /// <summary>
        /// Favourites JIDs from the warm cache (includes cold ids not yet on the chat list).
        /// </summary>
        IReadOnlyList<string> ListFavoriteJids();

        // Each write names one field. Writing the whole row instead means every caller has to be
        // holding a current copy of the other three, and the ones that were not silently reverted
        // them: muting a chat from the info panel used to store IsChatPinned=false over a pin.

        /// <summary>The chat-list pin, mirrored from pin_v1 / app-state.</summary>
        Task SetChatPinnedAsync(string jid, bool pinned, long? pinnedTimestamp = null);

        /// <summary>The Start-screen tile.</summary>
        Task SetWidgetPinnedAsync(string jid, bool pinned);

        /// <summary>WhatsApp Favourites list membership (favoritesAction).</summary>
        Task SetFavoriteAsync(string jid, bool favorite);

        /// <summary>Unix seconds mute deadline; null unmutes.</summary>
        Task SetMutedUntilAsync(string jid, long? mutedUntil);

        Task SetStatusAsync(string jid, ChatStatus status);

        /// <summary>This account's role in a channel, as the server last answered it.</summary>
        Task SetChannelRoleAsync(string jid, ChannelViewerRole role);

        /// <summary>
        /// The group description as the last <c>w:g2</c> reply or description notification said it.
        /// Pass an empty string for a group whose description was cleared.
        /// </summary>
        Task SetGroupDescriptionAsync(string jid, string description, string descriptionId);

        /// <summary>
        /// Writes the pin into the in-memory cache immediately. History sync applies flags onto
        /// the row and then persists asynchronously — without this, the next
        /// <see cref="ApplyTo"/> (batch release / tip refresh) reads a stale cache and clears the pin.
        /// </summary>
        void RememberChatPinned(string jid, bool pinned, long? pinnedTimestamp = null);

        /// <summary>Same as <see cref="RememberChatPinned"/> for Favourites.</summary>
        void RememberFavorite(string jid, bool favorite);

        /// <summary>
        /// Same as <see cref="RememberChatPinned"/> for the mute deadline.
        /// </summary>
        void RememberMutedUntil(string jid, long? mutedUntil);

        /// <summary>
        /// Applies SQLite local fields onto a chat model (widget pin, chat-list pin, mute).
        /// </summary>
        void ApplyTo(ChatItem chat);

        Task ApplyToAsync(ChatItem chat);
    }
}
