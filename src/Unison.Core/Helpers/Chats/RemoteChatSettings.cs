// =============================================================================
// RemoteChatSettings
//
// One chat's account-side settings (archive / pin / mute / read) as they arrive
// from app-state, in a shape that folds: a sync burst can carry several mutations
// for the same chat, and only the last value of each field should reach the UI.
// =============================================================================
namespace Unison.Core.Helpers.Chats
{
    public struct RemoteChatSettings
    {
        public bool? Archived;

        public bool AppliesPin;
        public bool IsPinned;
        public long? PinnedTimestamp;

        /// <summary>When true, <see cref="MutedUntil"/> null means unmute.</summary>
        public bool AppliesMute;
        public long? MutedUntil;

        public bool? Read;

        public bool TouchesFlags => Archived.HasValue || AppliesPin || AppliesMute;

        /// <summary>Field by field: whatever <paramref name="incoming"/> sets wins, the rest of <paramref name="pending"/> stays.</summary>
        public static RemoteChatSettings Merge(RemoteChatSettings pending, RemoteChatSettings incoming)
        {
            RemoteChatSettings merged = pending;
            if (incoming.Archived.HasValue)
            {
                merged.Archived = incoming.Archived;
            }

            if (incoming.AppliesPin)
            {
                merged.AppliesPin = true;
                merged.IsPinned = incoming.IsPinned;
                merged.PinnedTimestamp = incoming.PinnedTimestamp;
            }

            if (incoming.AppliesMute)
            {
                merged.AppliesMute = true;
                merged.MutedUntil = incoming.MutedUntil;
            }

            if (incoming.Read.HasValue)
            {
                merged.Read = incoming.Read;
            }

            return merged;
        }
    }
}
