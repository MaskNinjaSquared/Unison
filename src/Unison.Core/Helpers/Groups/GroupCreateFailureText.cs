// =============================================================================
// GroupCreateFailureText
//
// Resource key + English fallback for a refused group / community create, so
// the New Conversation form says why instead of a generic error.
// =============================================================================
using Unison.Core.Models;

namespace Unison.Core.Helpers.Groups
{
    public static class GroupCreateFailureText
    {
        public static void Resolve(string reason, bool isCommunity, out string key, out string fallback)
        {
            switch (reason)
            {
                case GroupCreateResult.ReasonEmptySubject:
                    if (isCommunity)
                    {
                        key = "CreateCommunity_Error_NameRequired";
                        fallback = "Enter a community name.";
                    }
                    else
                    {
                        key = "CreateGroup_Error_NameRequired";
                        fallback = "Enter a group name.";
                    }

                    return;
                case GroupCreateResult.ReasonNotConnected:
                    key = "CreateFlow_Error_NotConnected";
                    fallback = "You're not connected. Try again when the connection is back.";
                    return;
            }

            if (isCommunity)
            {
                key = "CreateCommunity_Error_Failed";
                fallback = "Couldn't create the community. Try again.";
            }
            else
            {
                key = "CreateGroup_Error_Failed";
                fallback = "Couldn't create the group. Try again.";
            }
        }
    }
}
