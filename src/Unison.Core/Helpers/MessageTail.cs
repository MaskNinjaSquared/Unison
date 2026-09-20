using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.Core.Helpers
{
    /// <summary>
    /// The newest messages of a conversation that can be named in a receipt.
    /// </summary>
    /// <remarks>
    /// Marking a conversation read and deleting it both send the server a range of ids rather
    /// than a single one, and both only care about the tail. A message with no id cannot go in
    /// that range, so it has to be dropped before the tail is measured - counting the whole
    /// list and then skipping over the filtered one overshoots by however many were dropped,
    /// and with enough of them the tail comes back empty and the receipt is never sent.
    /// </remarks>
    public static class MessageTail
    {
        public static List<ChatMessage> Addressable(IReadOnlyList<ChatMessage> messages, int take)
        {
            var result = new List<ChatMessage>();
            if (messages == null || take <= 0)
            {
                return result;
            }

            var addressable = new List<ChatMessage>(messages.Count);
            for (int i = 0; i < messages.Count; i++)
            {
                ChatMessage message = messages[i];
                if (message != null && !string.IsNullOrEmpty(message.Id))
                {
                    addressable.Add(message);
                }
            }

            int start = addressable.Count > take ? addressable.Count - take : 0;
            for (int i = start; i < addressable.Count; i++)
            {
                result.Add(addressable[i]);
            }

            return result;
        }
    }
}
