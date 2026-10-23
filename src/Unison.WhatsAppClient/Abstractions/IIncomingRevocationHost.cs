// =============================================================================
// IIncomingRevocationHost
//
// Timeline / list-tip half of inbound revoke. MessageFacade owns the apply
// order (envelope cleanup, find, tombstone, persist, notify); the client still
// holds MessagesByChat and the LocalizedChatStripLabels tip refresh.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Models;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IIncomingRevocationHost
    {
        /// <summary>
        /// In-memory chat keys that collapse to the same conversation as
        /// <paramref name="canonical"/>.
        /// </summary>
        IReadOnlyList<string> GetAliasCacheKeys(string canonical);

        void RemoveCachedMessage(string chatKey, string messageId);

        bool TryFindCachedMessage(
            IReadOnlyList<string> aliasKeys,
            string messageId,
            out ChatMessage message);

        /// <summary>
        /// Runs on the attached UI thread when one exists; otherwise inline so
        /// tests / pre-frame paths still apply PropertyChanged mutations.
        /// </summary>
        Task RunBoundMutationAsync(Action action);

        /// <summary>Newest tip under the canonical cache key, or null.</summary>
        ChatMessage TryPickLatestCachedTip(string canonical);

        /// <summary>
        /// List-strip refresh for a revoked tip. No-op when the chat façade is
        /// not attached yet.
        /// </summary>
        Task RefreshChatPreviewFromMessageAsync(string canonical, ChatMessage message);
    }
}
