// =============================================================================
// SentMessageRetryRebuild
//
// When a peer asks us to resend a message whose bytes left the socket cache,
// rebuild from the persisted chat row — but only for plain text. Media is stored
// as download keys, not the original stanza; answering with the wrong bytes is
// worse than declining and letting the peer ask the phone.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    public static class SentMessageRetryRebuild
    {
        public static global::Proto.Message TryCreate(ChatMessage stored)
        {
            if (stored == null || !stored.IsFromMe || string.IsNullOrEmpty(stored.Content))
            {
                return null;
            }

            if (stored.Kind != ChatMessageKind.Text)
            {
                return null;
            }

            return new global::Proto.Message { Conversation = stored.Content };
        }
    }
}
