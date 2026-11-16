using System;
using System.Threading;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>
    /// What <see cref="Helpers.HistoryCatchUpHost"/> still needs from the connection owner: health-monitor
    /// stop, status publishing, the post-message enrichment entry, the startup quiet wait and the
    /// catalogue persist switch.
    /// </summary>
    public interface IHistoryCatchUpHostPort
    {
        void StopConnectionHealthMonitor(string reason);

        void PublishConnectionStatus(string status);

        void TrySchedulePostMessageEnrichment(string reason);

        Task<bool> WaitForStartupQuietAsync(TimeSpan budget, string reason, CancellationToken token);

        void EnableScheduledPersist(string reason);
    }
}
