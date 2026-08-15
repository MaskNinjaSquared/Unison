using System;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Persistent local settings (Giuraffe/Imgur style). Keys live in
    /// <see cref="Constants.LocalSettingsConstants"/>.
    /// </summary>
    public interface ILocalSettings
    {
        /// <summary>Raised when a setting is written.</summary>
        event EventHandler<string> SettingSet;

        /// <summary>
        /// Retrieves the value for the key, or the default from
        /// <see cref="Constants.LocalSettingsConstants.Defaults"/>.
        /// </summary>
        T Get<T>(string settingKey);

        /// <summary>Saves a value into persistent local storage.</summary>
        void Set<T>(string settingKey, T value);

        bool ContainsKey(string settingKey);

        void Remove(string settingKey);
    }
}
