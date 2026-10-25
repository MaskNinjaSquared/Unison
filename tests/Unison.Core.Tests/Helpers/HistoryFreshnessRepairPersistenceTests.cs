using System;
using System.Collections.Generic;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class HistoryFreshnessRepairPersistenceTests
    {
        [Fact]
        public void Load_and_persist_roundtrip_stamps()
        {
            var settings = new MemorySettings();
            var persist = new HistoryFreshnessRepairPersistence(settings);

            DateTime repair = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            DateTime fallback = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            persist.PersistFullHistoryRepairCompletedUtc(repair);
            persist.PersistFreshnessReconnectFallbackUtc(fallback);

            Assert.Equal(repair, persist.LastFullHistoryRepairCompletedUtc);
            Assert.Equal(fallback, persist.LastFreshnessReconnectFallbackUtc);

            var reloaded = new HistoryFreshnessRepairPersistence(settings);
            reloaded.Load();
            Assert.Equal(repair, reloaded.LastFullHistoryRepairCompletedUtc);
            Assert.Equal(fallback, reloaded.LastFreshnessReconnectFallbackUtc);
        }

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
    }
}
