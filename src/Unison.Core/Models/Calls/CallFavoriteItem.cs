// =============================================================================
// CallFavoriteItem
//
// One row in the Calls tab Favourites preview (top 5 / Ver tudo).
// =============================================================================
using Unison.Core.Contracts;

namespace Unison.Core.Models
{
    public sealed class CallFavoriteItem
    {
        public CallFavoriteItem(ChatItem chat, IStringResources strings)
        {
            Chat = chat;
            Jid = chat?.JID;
            DisplayName = chat?.GetNameResolved(strings) ?? chat?.Name ?? chat?.JID ?? string.Empty;
            AvatarUrl = chat?.GetAvatarUrl(preferHigh: false);
        }

        public ChatItem Chat { get; }

        public string Jid { get; }

        public string DisplayName { get; }

        public string AvatarUrl { get; }
    }
}
