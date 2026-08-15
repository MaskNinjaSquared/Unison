// =============================================================================
// HistorySyncConfigFactory
//
// The history capabilities this client declares.
//
// The phone reads them twice: once at login, inside the device props, and again
// on every full-history request. The two have to agree - a request that claims
// support the login did not is answered with a chunk this client cannot read -
// which is the whole reason the shape lives here instead of being written out
// at each call site.
//
// Ports: rc14 the historySyncConfig block of DEFAULT_CONNECTION_CONFIG in
// src/Defaults/index.ts
// =============================================================================
namespace Unison.Socket.Sync
{
    public static class HistorySyncConfigFactory
    {
        /// <summary>
        /// What this client can be sent. Group history and call logs are declined: neither is
        /// shown anywhere, and asking for them only makes the sync slower.
        /// </summary>
        public static global::Proto.DeviceProps.Types.HistorySyncConfig Create()
        {
            return new global::Proto.DeviceProps.Types.HistorySyncConfig
            {
                StorageQuotaMb = 10240,
                InlineInitialPayloadInE2EeMsg = true,
                SupportCallLogHistory = false,
                SupportBotUserAgentChatHistory = true,
                SupportCagReactionsAndPolls = true,
                SupportBizHostedMsg = true,
                SupportRecentSyncChunkMessageCountTuning = true,
                SupportHostedGroupMsg = true,
                SupportFbidBotChatHistory = true,
                SupportMessageAssociation = true,
                SupportGroupHistory = false
            };
        }
    }
}
