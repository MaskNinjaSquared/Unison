namespace Unison.Core.Helpers
{
    /// <summary>
    /// WP8-style SemanticZoom jump cell: always A–Z + #; empty letters stay visible but disabled.
    /// </summary>
    public sealed class AlphabetJumpTile
    {
        public AlphabetJumpTile(string key, AlphabetChatGroup group)
        {
            Key = string.IsNullOrEmpty(key) ? "#" : key;
            Group = group;
            IsEnabled = group != null && group.Count > 0;
        }

        public string Key { get; }

        /// <summary>Populated group when <see cref="IsEnabled"/>; otherwise null.</summary>
        public AlphabetChatGroup Group { get; }

        public bool IsEnabled { get; }

        /// <summary>1.0 when enabled; dimmed when the letter has no favourites.</summary>
        public double Opacity => IsEnabled ? 1.0 : 0.35;

        public override string ToString() => Key;
    }
}
