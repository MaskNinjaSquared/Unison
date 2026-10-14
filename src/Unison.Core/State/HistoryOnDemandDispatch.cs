// =============================================================================
// HistoryOnDemandDispatch
//
// Asks the phone for history it still owes us. A chat page is anchored on the
// oldest message the timeline holds; the registry decides whether that anchor
// may be asked now, and a send that throws gives the chat's slot back (the
// anchor stays, so the same page waits for the next connect).
//
// The registry's lock guards every transition. Timers, banners and the answer
// itself stay with the caller.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Models;

namespace Unison.Core.State
{
    public sealed class HistoryOnDemandDispatch
    {
        private readonly HistoryOnDemandRegistry _registry;
        private readonly IHistoryOnDemandSocket _socket;
        private readonly Action<string> _log;

        public HistoryOnDemandDispatch(
            HistoryOnDemandRegistry registry,
            IHistoryOnDemandSocket socket,
            Action<string> log)
        {
            _registry = registry ?? throw new ArgumentNullException(nameof(registry));
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _log = log ?? (_ => { });
        }

        /// <summary>True when a request went out.</summary>
        /// <param name="chatJid">Already normalized.</param>
        /// <param name="timeline">The chat's messages, oldest first.</param>
        public async Task<bool> RequestChatPageAsync(
            string chatJid,
            IReadOnlyList<ChatMessage> timeline,
            int count,
            string triggerReason,
            DateTime nowUtc)
        {
            if (string.IsNullOrWhiteSpace(chatJid) || !_socket.IsReady)
            {
                return false;
            }

            if (timeline == null || timeline.Count == 0)
            {
                return false;
            }

            ChatMessage oldest = timeline[0];
            if (oldest == null || string.IsNullOrWhiteSpace(oldest.Id))
            {
                return false;
            }

            long oldestMs = UnixTime.ToMilliseconds(oldest.Timestamp);
            string marker = oldest.Id + "|" + oldest.IsFromMe + "|" + oldestMs;
            HistoryOnDemandChatGate gate = _registry.TryBeginChat(chatJid, marker, nowUtc, out DateTime backoffUntilUtc);
            if (gate == HistoryOnDemandChatGate.BackedOff)
            {
                _log($"HISTORY_SYNC_ON_DEMAND skipped for {chatJid}: backoff active until {backoffUntilUtc:O}");
            }

            if (gate != HistoryOnDemandChatGate.Started)
            {
                return false;
            }

            string requestId = null;
            try
            {
                requestId = _socket.GenerateMessageId();
                int baselineCount = timeline.Count;
                _registry.RegisterChat(chatJid, requestId, marker, baselineCount, triggerReason, nowUtc);

                string sentRequestId = await _socket.RequestChatHistoryAsync(
                    chatJid,
                    oldest.Id,
                    oldest.IsFromMe,
                    oldestMs,
                    count,
                    requestId).ConfigureAwait(false);
                if (!string.Equals(sentRequestId, requestId, StringComparison.Ordinal))
                {
                    _log($"HISTORY_SYNC_ON_DEMAND stanza id changed unexpectedly: tracked={requestId}, sent={sentRequestId}");
                }

                _log($"Requested HISTORY_SYNC_ON_DEMAND for {chatJid} (requestId={requestId}, oldestId={oldest.Id}, baseline={baselineCount}, trigger={triggerReason ?? "unspecified"})");
                return true;
            }
            catch (Exception ex)
            {
                _registry.FailChat(chatJid, requestId);
                _log($"HISTORY_SYNC_ON_DEMAND request failed for {chatJid}: {ex.Message}");
                return false;
            }
        }
    }
}
