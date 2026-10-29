using System.Collections.Generic;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Helpers;
using Unison.Core.Models;
using Xunit;

namespace Unison.Core.Tests.Helpers
{
    public class EmojiRecentStoreTests
    {
        [Fact]
        public void RecordFromSentText_bumps_known_glyphs_once_and_persists()
        {
            var settings = new MemorySettings();
            var store = new EmojiRecentStore(settings);

            store.RecordFromSentText("hi 😀 and 🔥 then 😀 again");

            Assert.Equal(new[] { "🔥", "😀" }, store.Recents);
            Assert.Contains("\u001f", settings.Get<string>(LocalSettingsConstants.RecentEmojis));
        }

        [Fact]
        public void RecordFromSentText_ignores_unknown_and_blank()
        {
            var settings = new MemorySettings();
            var store = new EmojiRecentStore(settings);

            store.RecordFromSentText("no emoji here");
            store.RecordFromSentText(null);
            store.RecordFromSentText(string.Empty);

            Assert.Empty(store.Recents);
        }

        [Fact]
        public void Boot_reloads_the_persisted_list()
        {
            var settings = new MemorySettings();
            settings.Set(LocalSettingsConstants.RecentEmojis, "🎉\u001f❤️");

            var store = new EmojiRecentStore(settings);

            Assert.Equal(new[] { "🎉", "❤️" }, store.Recents);
        }

        [Fact]
        public void Parse_skips_blanks_and_caps_at_max()
        {
            var parts = new List<string>();
            for (int i = 0; i < EmojiRecentStore.MaxRecents + 5; i++)
            {
                parts.Add("e" + i);
            }

            List<string> parsed = EmojiRecentStore.Parse(string.Join("\u001f", parts));

            Assert.Equal(EmojiRecentStore.MaxRecents, parsed.Count);
            Assert.Equal("e0", parsed[0]);
        }

        [Fact]
        public void CollectKnownGlyphsInText_returns_first_seen_catalog_order()
        {
            IReadOnlyList<string> found = EmojiCatalog.CollectKnownGlyphsInText("x😀y🔥😀z");

            Assert.Equal(new[] { "😀", "🔥" }, found);
        }

        [Fact]
        public void BuiltInCategoriesFor_keeps_flags_on_every_device()
        {
            IReadOnlyList<EmojiCategory> low = EmojiCatalog.BuiltInCategoriesFor(DeviceCategory.Low);
            IReadOnlyList<EmojiCategory> high = EmojiCatalog.BuiltInCategoriesFor(DeviceCategory.High);
            IReadOnlyList<EmojiCategory> unsupported =
                EmojiCatalog.BuiltInCategoriesFor(DeviceCategory.Unsupported);

            Assert.Equal(EmojiCatalog.BuiltInCategories.Count, high.Count);
            Assert.Equal(high.Count, low.Count);
            Assert.Equal(high.Count, unsupported.Count);
            Assert.Contains(low, c => c.Id == EmojiCatalog.FlagsId);
            Assert.Contains(high, c => c.Id == EmojiCatalog.FlagsId);
        }

        private sealed class MemorySettings : ILocalSettings
        {
            private readonly Dictionary<string, object> _values =
                new Dictionary<string, object>();

            public T Get<T>(string settingKey)
            {
                object value;
                if (_values.TryGetValue(settingKey, out value) && value is T typed)
                {
                    return typed;
                }

                object def;
                if (LocalSettingsConstants.Defaults.TryGetValue(settingKey, out def) && def is T typedDef)
                {
                    return typedDef;
                }

                return default(T);
            }

            public void Set<T>(string settingKey, T value) => _values[settingKey] = value;

            public bool ContainsKey(string settingKey) => _values.ContainsKey(settingKey);

            public void Remove(string settingKey) => _values.Remove(settingKey);

            public event System.EventHandler<string> SettingSet;
        }
    }
}
