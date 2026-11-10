// =============================================================================
// ChatListCatalogAccess
//
// Shared predicates and frugal/desktop policies for chat-list catalog reads.
// Architecture is the same on every device (singleton catalog context + scope
// filter); only populate/defer aggressiveness differs by PreferFrugalUi.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Chats
{
    public static class ChatListCatalogAccess
    {
        /// <summary>
        /// Active list needs hub collapse; Channels/Archived are flat membership filters.
        /// </summary>
        public static bool NeedsCommunityProjection(ChatListScope scope) =>
            scope == ChatListScope.Active;

        /// <summary>
        /// Frugal Mobile defers cold Active rebuilds while enrichment owns the dispatcher.
        /// Channels/Archived stay immediate (cheap filter) on every device.
        /// </summary>
        public static bool ShouldDeferAttachRefresh(
            bool preferFrugalUi,
            bool enrichmentPhase,
            ChatListScope scope) =>
            preferFrugalUi &&
            enrichmentPhase &&
            NeedsCommunityProjection(scope);

        /// <summary>Whether a catalogue row belongs under <paramref name="scope"/>.</summary>
        public static bool MatchesScope(ChatItem chat, ChatListScope scope)
        {
            if (chat == null)
            {
                return false;
            }

            switch (scope)
            {
                case ChatListScope.Archived:
                    return chat.Status == ChatStatus.Archived;

                case ChatListScope.Channels:
                    return chat.Status == ChatStatus.Active && chat.IsChannel;

                case ChatListScope.Active:
                default:
                    return chat.Status == ChatStatus.Active && !chat.IsChannel;
            }
        }
    }
}
