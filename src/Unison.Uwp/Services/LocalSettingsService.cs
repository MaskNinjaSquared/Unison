// =============================================================================
// LocalSettingsService
//
// ILocalSettings over ApplicationData.LocalSettings, with reads kept in RAM:
// each Values[] access is a WinRT call, and some keys are read per render.
// Set / Remove drop the cached entry so the next read sees what WinRT stored.
//
// The cache is per process. A key another process writes (Unison.Background)
// must be read straight from ApplicationData, never through this service.
// =============================================================================
using System;
using System.Collections.Concurrent;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    public class LocalSettingsService : ILocalSettings
    {
        private static readonly object Missing = new object();

        // Static: LocalSettingsAccess keeps a pre-DI fallback instance; both must see one cache.
        private static readonly object _writeGate = new object();

        private static readonly ConcurrentDictionary<string, object> _values =
            new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        public event EventHandler<string> SettingSet;

        public T Get<T>(string settingKey)
        {
            object result = Read(settingKey);
            if (result == null)
                return (T)LocalSettingsConstants.Defaults[settingKey];
            return (T)result;
        }

        public void Set<T>(string settingKey, T value)
        {
            lock (_writeGate)
            {
                ApplicationData.Current.LocalSettings.Values[settingKey] = value;
                _values.TryRemove(settingKey, out _);
            }

            SettingSet?.Invoke(this, settingKey);
        }

        public bool ContainsKey(string settingKey) => Read(settingKey) != null;

        public void Remove(string settingKey)
        {
            lock (_writeGate)
            {
                ApplicationData.Current.LocalSettings.Values.Remove(settingKey);
                _values.TryRemove(settingKey, out _);
            }

            SettingSet?.Invoke(this, settingKey);
        }

        private object Read(string settingKey)
        {
            object cached;
            if (_values.TryGetValue(settingKey, out cached))
            {
                return ReferenceEquals(cached, Missing) ? null : cached;
            }

            // Under the write gate: a miss racing a Set must not cache the value Set replaced.
            lock (_writeGate)
            {
                object stored = ApplicationData.Current.LocalSettings.Values[settingKey];
                _values[settingKey] = stored ?? Missing;
                return stored;
            }
        }
    }
}
