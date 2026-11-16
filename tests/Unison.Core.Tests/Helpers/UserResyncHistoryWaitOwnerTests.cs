using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.WhatsAppClient.Helpers;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public sealed class UserResyncHistoryWaitOwnerTests
    {
        [Fact]
        public async Task Wait_completes_when_history_lands_and_clears_repair_latch()
        {
            var settings = new MemorySettings();
            settings.Set(LocalSettingsConstants.MessageStoreForceHistoryRepair, true);
            var owner = new UserResyncHistoryWaitOwner(
                _ => { },
                _ => { },
                _ => Task.FromResult(true),
                settings);

            Task wait = owner.WaitAsync();
            Assert.True(owner.IsWaiting);

            owner.Complete("history-landed");
            await wait.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(owner.IsWaiting);
            Assert.False(settings.Get<bool>(LocalSettingsConstants.MessageStoreForceHistoryRepair));
        }

        [Fact]
        public async Task Wait_disarms_when_full_history_request_fails()
        {
            var statuses = new List<string>();
            var owner = new UserResyncHistoryWaitOwner(
                _ => { },
                statuses.Add,
                _ => Task.FromResult(false),
                new MemorySettings());

            await owner.WaitAsync();

            Assert.False(owner.IsWaiting);
            Assert.False(owner.TryFail());
            Assert.Contains("Could not start history download. Try again.", statuses);
        }

        [Fact]
        public async Task TryFail_releases_an_armed_waiter()
        {
            var owner = new UserResyncHistoryWaitOwner(
                _ => { },
                _ => { },
                _ => Task.FromResult(true),
                new MemorySettings());

            Task wait = owner.WaitAsync();

            Assert.True(owner.TryFail());
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(owner.IsWaiting);
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
