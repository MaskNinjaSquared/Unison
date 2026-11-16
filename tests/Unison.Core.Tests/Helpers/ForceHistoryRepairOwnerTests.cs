using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class ForceHistoryRepairOwnerTests
    {
        [Fact]
        public void MarkPending_sets_latch_and_journals()
        {
            var settings = new MemorySettings();
            var diagnostics = new RecordingDiagnostics();
            var owner = Create(settings, diagnostics, (_, __) => Task.FromResult(true));

            owner.MarkPending("adjacent-cap");

            Assert.True(settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair));
            Assert.Contains("force-history-repair-latched", diagnostics.Events);
        }

        [Fact]
        public async Task Consume_requests_tracked_full_history_and_clears_latch()
        {
            var settings = new MemorySettings();
            settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);
            string requestedReason = null;
            bool? freshnessRepair = null;
            var owner = Create(settings, new RecordingDiagnostics(), (reason, isFreshnessRepair) =>
            {
                requestedReason = reason;
                freshnessRepair = isFreshnessRepair;
                return Task.FromResult(true);
            });

            await owner.ConsumeAsync("session-open");

            Assert.Equal("message-store-epoch:session-open", requestedReason);
            Assert.True(freshnessRepair);
            Assert.False(settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair));
        }

        [Fact]
        public async Task Consume_keeps_latch_when_request_is_not_sent()
        {
            var settings = new MemorySettings();
            settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);
            var owner = Create(settings, new RecordingDiagnostics(), (_, __) => Task.FromResult(false));

            await owner.ConsumeAsync("session-open");

            Assert.True(settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair));
        }

        [Fact]
        public async Task Consume_skips_request_without_latch()
        {
            int requests = 0;
            var owner = Create(new MemorySettings(), new RecordingDiagnostics(), (_, __) =>
            {
                requests++;
                return Task.FromResult(true);
            });

            await owner.ConsumeAsync("session-open");

            Assert.Equal(0, requests);
        }

        private static ForceHistoryRepairOwner Create(
            ILocalSettings settings,
            IRuntimeDiagnostics diagnostics,
            Func<string, bool, Task<bool>> requestFull) =>
            new ForceHistoryRepairOwner(
                settings,
                () => null,
                requestFull,
                diagnostics,
                _ => { });

        private sealed class MemorySettings : ILocalSettings
        {
            private readonly Dictionary<string, object> _map =
                new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            public event EventHandler<string> SettingSet;

            public T Get<T>(string settingKey)
            {
                object value;
                if (_map.TryGetValue(settingKey, out value) && value is T)
                {
                    return (T)value;
                }

                return default(T);
            }

            public void Set<T>(string settingKey, T value)
            {
                _map[settingKey] = value;
                SettingSet?.Invoke(this, settingKey);
            }

            public bool ContainsKey(string settingKey) => _map.ContainsKey(settingKey);

            public void Remove(string settingKey) => _map.Remove(settingKey);
        }

        private sealed class RecordingDiagnostics : IRuntimeDiagnostics
        {
            public List<string> Events { get; } = new List<string>();

            public bool IsVerboseLoggingEnabled => false;

            public void SetVerboseLogging(bool enabled, string source)
            {
            }

            public void Start()
            {
            }

            public void StartHealthSampling()
            {
            }

            public void Write(string category, string eventName, string details = null) => Events.Add(eventName);

            public void RecordException(string category, string eventName, Exception exception, string details = null) =>
                Events.Add(eventName);

            public string GetRecentText() => string.Empty;

            public RuntimeDiagnosticsSnapshot CaptureSnapshot() => null;

            public Task FlushAsync(string reason) => Task.CompletedTask;

            public Task<string> ExportReportAsync() => Task.FromResult(string.Empty);

            public Task<string> SaveReportToLocalFolderAsync() => Task.FromResult(string.Empty);

            public Task ClearAsync() => Task.CompletedTask;
        }
    }
}
