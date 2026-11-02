using System;
using System.Collections.Generic;

namespace Unison.Core.Contracts
{
    /// <summary>
    /// Recently used emoji glyphs, loaded into memory at construction and shared
    /// across chat opens. Persistence is a short LocalSettings string — not SQLite.
    /// </summary>
    public interface IEmojiRecentStore
    {
        /// <summary>Most-recent first. Empty until a sent message contributes glyphs.</summary>
        IReadOnlyList<string> Recents { get; }

        /// <summary>
        /// After a successful send: pull known catalog glyphs out of
        /// <paramref name="text"/>, bump them in the MRU, persist once.
        /// No-op while the user is only tapping the picker.
        /// </summary>
        void RecordFromSentText(string text);

        event EventHandler Changed;
    }
}
