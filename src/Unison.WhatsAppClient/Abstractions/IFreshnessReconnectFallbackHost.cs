// =============================================================================
// IFreshnessReconnectFallbackHost
//
// Side effects for FreshnessReconnectFallbackDispatch (schedule + delayed reconnect).
// =============================================================================
using System;
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    public interface IFreshnessReconnectFallbackHost
    {
        bool FreshnessReconnectFallbackInProgress { get; set; }

        bool SuppressReconnect { get; }

        bool IsAuthRegistered { get; }

        DateTime LastFreshnessReconnectFallbackUtc { get; }

        TimeSpan FreshnessReconnectFallbackCooldown { get; }

        /// <summary>Production uses <see cref="FreshnessReconnectFallbackGates.ReconnectDelay"/>.</summary>
        TimeSpan FreshnessReconnectDelay { get; }

        bool TryGetFreshnessStaleReason(DateTime nowUtc, out string staleReason);

        void PersistFreshnessReconnectFallbackUtc(DateTime utc);

        void LogFreshnessFallback(string message);

        void StartHydrationFallbackPipeline(string reason);

        void PublishFreshnessReconnectStatus(string status);

        void StopConnectionHealthMonitor(string reason);

        void TearDownSocketForReconnect();

        void ScheduleAutoReconnect(string reason);

        void RaiseFreshnessFallbackError(Exception error);

        /// <summary>Host owns Task.Run so UI/socket affinity stays with WhatsAppService.</summary>
        void FireAndForgetFreshnessReconnect(Func<Task> work);
    }
}
