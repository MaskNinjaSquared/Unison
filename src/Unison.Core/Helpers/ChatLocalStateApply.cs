using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// Puts the stored local state of a conversation back onto its row: the chat-list pin, the
    /// mute deadline, the Start-screen tile, and archived as a fallback.
    /// </summary>
    /// <remarks>
    /// This is the only durable home for pin and mute — <c>history_chat_preview</c> has no columns
    /// for either — so it is what stands between a restart and a list with every icon missing.
    ///
    /// Only fields the store <em>knows</em> are applied. A mute-only cache stub used to carry
    /// <c>IsChatPinned = false</c> by default; applying that default mid-sync wiped favourites.
    /// </remarks>
    public static class ChatLocalStateApply
    {
        /// <summary>Sort key for a pin whose real timestamp has not arrived from app-state yet.</summary>
        private const long PendingPinTimestamp = 1;

        /// <summary>An unpin, written explicitly so a sibling address cannot resurrect the pin.</summary>
        private const long UnpinnedTimestamp = 0;

        public static void Apply(ChatItem chat, ChatLocalState state)
        {
            if (chat == null)
            {
                return;
            }

            if (state == null)
            {
                // Nothing stored says nothing about pin or mute: app-state can apply either before
                // a row exists, and reading absence as "off" would undo it. The tile is different —
                // it is on the Start screen or it is not.
                chat.IsWidgetPinned = false;
                return;
            }

            // history_chat_preview owns status; this table only answers when app-state arrived
            // before the catalogue had a row to put it on.
            if (state.Knows(ChatLocalStateFields.Status) &&
                chat.Status == ChatStatus.Active &&
                state.Status != ChatStatus.Active)
            {
                chat.Status = state.Status;
            }

            if (state.Knows(ChatLocalStateFields.WidgetPin))
            {
                chat.IsWidgetPinned = state.IsWidgetPinned;
            }

            if (state.Knows(ChatLocalStateFields.Mute))
            {
                chat.MutedUntil = state.MutedUntil;
            }

            if (!state.Knows(ChatLocalStateFields.Pin))
            {
                return;
            }

            chat.IsChatPinned = state.IsChatPinned;

            if (!state.IsChatPinned)
            {
                chat.PinnedTimestamp = UnpinnedTimestamp;
            }
            else if (chat.PinnedTimestamp == null || chat.PinnedTimestamp == 0)
            {
                chat.PinnedTimestamp = PendingPinTimestamp;
            }
        }
    }
}
