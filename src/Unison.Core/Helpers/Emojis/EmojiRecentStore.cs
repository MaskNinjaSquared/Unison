// =============================================================================
// EmojiRecentStore
//
// Short MRU list of emoji glyphs for the sticker box. Lives in LocalSettings as
// a unit-separated string so boot can hydrate memory without SQLite.
// Updates only via RecordFromSentText (after send) — never on picker taps.
// =============================================================================
using System;
using System.Collections.Generic;
using Unison.Core.Constants;
using Unison.Core.Contracts;

namespace Unison.Core.Helpers
{
    public sealed class EmojiRecentStore : IEmojiRecentStore
    {
        public const int MaxRecents = 32;

        private static readonly char Separator = '\u001f';

        private readonly ILocalSettings _settings;
        private readonly object _gate = new object();
        private List<string> _recents;

        public EmojiRecentStore(ILocalSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _recents = Parse(_settings.Get<string>(LocalSettingsConstants.RecentEmojis));
        }

        public IReadOnlyList<string> Recents
        {
            get
            {
                lock (_gate)
                {
                    return _recents.ToArray();
                }
            }
        }

        public event EventHandler Changed;

        public void RecordFromSentText(string text)
        {
            IReadOnlyList<string> found = EmojiCatalog.CollectKnownGlyphsInText(text);
            if (found == null || found.Count == 0)
            {
                return;
            }

            lock (_gate)
            {
                for (int i = 0; i < found.Count; i++)
                {
                    string glyph = found[i];
                    for (int r = _recents.Count - 1; r >= 0; r--)
                    {
                        if (string.Equals(_recents[r], glyph, StringComparison.Ordinal))
                        {
                            _recents.RemoveAt(r);
                        }
                    }
                }

                // Later glyphs in the message sit closer to the front (MRU).
                for (int i = 0; i < found.Count; i++)
                {
                    _recents.Insert(0, found[i]);
                }

                while (_recents.Count > MaxRecents)
                {
                    _recents.RemoveAt(_recents.Count - 1);
                }

                _settings.Set(
                    LocalSettingsConstants.RecentEmojis,
                    string.Join(Separator.ToString(), _recents));
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }

        public static List<string> Parse(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw))
            {
                return list;
            }

            string[] parts = raw.Split(Separator);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i]?.Trim();
                if (string.IsNullOrEmpty(part))
                {
                    continue;
                }

                if (!list.Contains(part))
                {
                    list.Add(part);
                }

                if (list.Count >= MaxRecents)
                {
                    break;
                }
            }

            return list;
        }
    }
}
