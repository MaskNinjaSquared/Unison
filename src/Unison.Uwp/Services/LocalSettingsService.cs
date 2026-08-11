using System;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Windows.Storage;

namespace Unison.Uwp.Services
{
    public class LocalSettingsService : ILocalSettings
    {
        public event EventHandler<string> SettingSet;

        public T Get<T>(string settingKey)
        {
            object result = ApplicationData.Current.LocalSettings.Values[settingKey];
            if (result == null)
                return (T)LocalSettingsConstants.Defaults[settingKey];
            return (T)result;
        }

        public void Set<T>(string settingKey, T value)
        {
            ApplicationData.Current.LocalSettings.Values[settingKey] = value;
            SettingSet?.Invoke(this, settingKey);
        }

        public bool ContainsKey(string settingKey)
            => ApplicationData.Current.LocalSettings.Values.ContainsKey(settingKey);

        public void Remove(string settingKey)
        {
            ApplicationData.Current.LocalSettings.Values.Remove(settingKey);
            SettingSet?.Invoke(this, settingKey);
        }
    }
}
