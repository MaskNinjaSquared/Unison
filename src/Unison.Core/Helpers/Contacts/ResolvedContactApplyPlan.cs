// =============================================================================
// ResolvedContactApplyPlan
//
// What one usync contact answer is allowed to change: the alias, a one-time
// merge of the two chats, the push name, and a missing avatar.
//
// The caches those writes land in still live on the client. This only decides;
// writing the alias, healing identity, merging and fetching stay with the caller.
// =============================================================================
namespace Unison.Core.Helpers
{
    public struct ResolvedContactApplyPlan
    {
        public bool WriteAlias;

        /// <summary>First time this LID was seen. A later answer must not merge again.</summary>
        public bool MergeDuplicateChats;

        public bool StorePushName;

        /// <summary>A listed chat is showing no picture. No row means there is nowhere to put one.</summary>
        public bool FetchAvatar;

        /// <summary>Alias or name changed, so the chat list has to be redrawn.</summary>
        public bool CachesChanged;

        public static ResolvedContactApplyPlan Decide(
            bool hasLid,
            bool lidAlreadyAliased,
            bool pairAccepted,
            bool hasPushName,
            bool listedChatHasNoAvatar)
        {
            bool writeAlias = hasLid && pairAccepted;
            return new ResolvedContactApplyPlan
            {
                WriteAlias = writeAlias,
                MergeDuplicateChats = writeAlias && !lidAlreadyAliased,
                StorePushName = hasPushName,
                FetchAvatar = listedChatHasNoAvatar,
                CachesChanged = writeAlias || hasPushName
            };
        }
    }
}
