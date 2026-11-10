// =============================================================================
// ChannelCreateFailureText
//
// Resource key + English fallback for a refused channel create, so the New
// Conversation form says why instead of a generic error.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Channels
{
    public static class ChannelCreateFailureText
    {
        public static void Resolve(string reason, out string key, out string fallback)
        {
            switch (reason)
            {
                case ChannelCreateResult.ReasonEmptyName:
                    key = "CreateChannel_Error_NameRequired";
                    fallback = "Enter a channel name.";
                    return;
                case ChannelCreateResult.ReasonNotConnected:
                    key = "CreateFlow_Error_NotConnected";
                    fallback = "You're not connected. Try again when the connection is back.";
                    return;
                default:
                    key = "CreateChannel_Error_Failed";
                    fallback = "Couldn't create the channel. Try again.";
                    return;
            }
        }
    }
}
