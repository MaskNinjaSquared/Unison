// =============================================================================
// HistorySyncWireApply
//
// OnHistorySyncReceived body: not gated on current socket (chunk is account
// past). Drop only when the session was wiped. Handler wiring stays on the host.
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Contracts.WhatsApp;
using Proto;

namespace Unison.WhatsAppClient.Helpers
{
    public static class HistorySyncWireApply
    {
        /// <summary>
        /// True when a wiped / missing auth session must drop the chunk.
        /// </summary>
        public static bool ShouldDrop(bool fatalSessionEnded, bool hasAuthState)
        {
            return fatalSessionEnded || !hasAuthState;
        }

        public static void Run(IHistorySyncWireHost host, HistorySync sync)
        {
            if (host == null)
            {
                throw new ArgumentNullException(nameof(host));
            }

            if (ShouldDrop(host.FatalSessionEnded, host.HasAuthState))
            {
                return;
            }

            int conversations = sync?.Conversations?.Count ?? 0;
            string syncType = sync?.SyncType.ToString() ?? "<none>";
            var started = Stopwatch.StartNew();
            host.WriteSyncApplyBegin(conversations, syncType);
            try
            {
                host.ApplyHistorySyncAsync(sync).GetAwaiter().GetResult();
            }
            finally
            {
                host.WriteSyncApplyEnd(conversations, started.ElapsedMilliseconds);
            }
        }
    }
}
