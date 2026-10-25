// =============================================================================
// HistoryFreshnessRepairPersistence
//
// Owns load/persist of full-history repair + freshness reconnect UTC stamps
// (three dispatch hosts that left WhatsAppService).
// =============================================================================
using System;
using System.Diagnostics;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Helpers
{
    public sealed class HistoryFreshnessRepairPersistence :
        ILoadHistoryFreshnessRepairStateHost,
        IPersistFullHistoryRepairCompletedUtcHost,
        IPersistFreshnessReconnectFallbackUtcHost
    {
        private readonly ILocalSettings _settings;
        private DateTime _lastFullHistoryRepairCompletedUtc = DateTime.MinValue;
        private DateTime _lastFreshnessReconnectFallbackUtc = DateTime.MinValue;

        public HistoryFreshnessRepairPersistence(ILocalSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public DateTime LastFullHistoryRepairCompletedUtc => _lastFullHistoryRepairCompletedUtc;

        public DateTime LastFreshnessReconnectFallbackUtc => _lastFreshnessReconnectFallbackUtc;

        public void Load() => LoadHistoryFreshnessRepairStateDispatch.Run(this);

        public void PersistFullHistoryRepairCompletedUtc(DateTime timestampUtc) =>
            PersistFullHistoryRepairCompletedUtcDispatch.Run(this, timestampUtc);

        public void PersistFreshnessReconnectFallbackUtc(DateTime timestampUtc) =>
            PersistFreshnessReconnectFallbackUtcDispatch.Run(this, timestampUtc);

        string ILoadHistoryFreshnessRepairStateHost.ReadLastFullHistoryRepairCompletedUtcRaw() =>
            _settings.Get<string>(LocalSettingsConstants.LastFullHistoryRepairCompletedUtc);

        string ILoadHistoryFreshnessRepairStateHost.ReadLastFreshnessReconnectFallbackUtcRaw() =>
            _settings.Get<string>(LocalSettingsConstants.LastFreshnessReconnectFallbackUtc);

        void ILoadHistoryFreshnessRepairStateHost.SetLastFullHistoryRepairCompletedUtc(
            DateTime timestampUtc) =>
            _lastFullHistoryRepairCompletedUtc = timestampUtc;

        void ILoadHistoryFreshnessRepairStateHost.SetLastFreshnessReconnectFallbackUtc(
            DateTime timestampUtc) =>
            _lastFreshnessReconnectFallbackUtc = timestampUtc;

        void ILoadHistoryFreshnessRepairStateHost.LogLoadedFullHistoryRepairCompleted(
            DateTime timestampUtc) =>
            Debug.WriteLine(
                "[HistoryFreshnessRepairPersistence] Loaded full-history repair completed: " +
                timestampUtc.ToString("O"));

        void ILoadHistoryFreshnessRepairStateHost.LogLoadedFreshnessReconnectFallback(
            DateTime timestampUtc) =>
            Debug.WriteLine(
                "[HistoryFreshnessRepairPersistence] Loaded freshness reconnect fallback: " +
                timestampUtc.ToString("O"));

        void ILoadHistoryFreshnessRepairStateHost.LogLoadHistoryFreshnessRepairStateFailed(
            string message) =>
            Debug.WriteLine(
                "[HistoryFreshnessRepairPersistence] Failed to load: " + message);

        void IPersistFullHistoryRepairCompletedUtcHost.SetLastFullHistoryRepairCompletedUtc(
            DateTime timestampUtc) =>
            _lastFullHistoryRepairCompletedUtc = timestampUtc;

        void IPersistFullHistoryRepairCompletedUtcHost.WriteLastFullHistoryRepairCompletedUtc(
            string roundtripText) =>
            _settings.Set(LocalSettingsConstants.LastFullHistoryRepairCompletedUtc, roundtripText);

        void IPersistFullHistoryRepairCompletedUtcHost.LogPersistFullHistoryRepairCompletedFailed(
            string message) =>
            Debug.WriteLine(
                "[HistoryFreshnessRepairPersistence] Persist full-history repair failed: " +
                message);

        void IPersistFreshnessReconnectFallbackUtcHost.SetLastFreshnessReconnectFallbackUtc(
            DateTime timestampUtc) =>
            _lastFreshnessReconnectFallbackUtc = timestampUtc;

        void IPersistFreshnessReconnectFallbackUtcHost.WriteLastFreshnessReconnectFallbackUtc(
            string roundtripText) =>
            _settings.Set(LocalSettingsConstants.LastFreshnessReconnectFallbackUtc, roundtripText);

        void IPersistFreshnessReconnectFallbackUtcHost.LogPersistFreshnessReconnectFallbackFailed(
            string message) =>
            Debug.WriteLine(
                "[HistoryFreshnessRepairPersistence] Persist freshness reconnect failed: " +
                message);
    }
}
