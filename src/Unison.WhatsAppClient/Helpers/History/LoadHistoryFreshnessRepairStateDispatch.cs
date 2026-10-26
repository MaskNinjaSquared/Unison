// =============================================================================
// LoadHistoryFreshnessRepairStateDispatch
//
// Body of LoadHistoryFreshnessRepairState. Local settings I/O stays on the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public static class LoadHistoryFreshnessRepairStateDispatch
    {
        public static void Run(ILoadHistoryFreshnessRepairStateHost host)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            try
            {
                string rawText = host.ReadLastFullHistoryRepairCompletedUtcRaw();
                if (HistoryFreshnessUtcText.TryParseRoundtrip(rawText, out DateTime parsed))
                {
                    host.SetLastFullHistoryRepairCompletedUtc(parsed);
                    host.LogLoadedFullHistoryRepairCompleted(parsed);
                }

                string reconnectRawText = host.ReadLastFreshnessReconnectFallbackUtcRaw();
                if (HistoryFreshnessUtcText.TryParseRoundtrip(reconnectRawText, out DateTime reconnectParsed))
                {
                    host.SetLastFreshnessReconnectFallbackUtc(reconnectParsed);
                    host.LogLoadedFreshnessReconnectFallback(reconnectParsed);
                }
            }
            catch (Exception ex)
            {
                host.LogLoadHistoryFreshnessRepairStateFailed(ex.Message);
            }
        }
    }
}
