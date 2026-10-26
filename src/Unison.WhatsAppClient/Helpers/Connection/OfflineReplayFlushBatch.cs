// =============================================================================
// OfflineReplayFlushBatch
//
// Pure id split for a flush batch (outgoing vs incoming pending journal rows).
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Helpers
{
    public static class OfflineReplayFlushBatch
    {
        public static void CollectPendingIds(
            IEnumerable<ChatMessage> batch,
            ISet<string> outgoingIds,
            ISet<string> incomingIds)
        {
            if (batch == null)
            {
                return;
            }

            if (outgoingIds == null)
            {
                throw new ArgumentNullException(nameof(outgoingIds));
            }

            if (incomingIds == null)
            {
                throw new ArgumentNullException(nameof(incomingIds));
            }

            foreach (ChatMessage message in batch)
            {
                if (message == null || string.IsNullOrWhiteSpace(message.Id))
                {
                    continue;
                }

                if (message.IsFromMe)
                {
                    outgoingIds.Add(message.Id);
                }
                else
                {
                    incomingIds.Add(message.Id);
                }
            }
        }
    }
}
