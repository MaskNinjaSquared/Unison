using System;
using System.Threading;
using System.Threading.Tasks;
using Unison.Uwp.Services.WhatsApp.History;
using Unison.WhatsAppClient.Abstractions;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService : IHistoryCatchUpHostPort
    {
        private bool IsHistoryCatchUpActive() => _historyCatchUp.IsHistoryCatchUpActive;

        void IConnectionSoftReconnectHost.ScheduleSoftReconnect(string reason) =>
            _softReconnect.Schedule(reason);

        /// <summary>
        /// Soft-reconnect to pull another offline catch-up lot while the history banner stays up.
        /// Distinct from the long-cooldown freshness failure fallback.
        /// </summary>
        void IConnectionSoftReconnectHost.ScheduleCatchUpContinueReconnect(string reason) =>
            _historyCatchUp.ReconnectFallback.ScheduleCatchUpContinue(reason);

        // --- IHistoryCatchUpHostPort -----------------------------------------

        void IHistoryCatchUpHostPort.StopConnectionHealthMonitor(string reason) =>
            StopConnectionHealthMonitor(reason);

        void IHistoryCatchUpHostPort.PublishConnectionStatus(string status) =>
            PublishConnectionUpdateDispatch.Run(this, status);

        void IHistoryCatchUpHostPort.TrySchedulePostMessageEnrichment(string reason) =>
            PostMessageEnrichmentDispatch.TrySchedule(this, reason);

        Task<bool> IHistoryCatchUpHostPort.WaitForStartupQuietAsync(
            TimeSpan budget,
            string reason,
            CancellationToken token) =>
            WaitForStartupQuietAsync(budget, reason, token);

        void IHistoryCatchUpHostPort.EnableScheduledPersist(string reason) =>
            EnableScheduledPersist(reason);

        /// <summary>
        /// Rebuilds a message we sent, so a retry receipt that arrives after the socket's cache
        /// has expired can still be answered.
        ///
        /// Only text is rebuilt. A media message is stored as its download keys rather than as
        /// the stanza that carried it, and a reconstruction would differ from what the peer is
        /// asking to see again - so it declines instead, and the peer falls back to asking our
        /// phone. Answering with the wrong bytes is worse than not answering.
        /// </summary>
        internal Task<global::Proto.Message> ResolveSentMessageForRetryAsync(
            string remoteJid,
            string messageId) =>
            _persistedLookup.ResolveSentForRetryAsync(remoteJid, messageId);
    }
}
