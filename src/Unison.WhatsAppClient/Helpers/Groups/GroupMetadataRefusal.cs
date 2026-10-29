// =============================================================================
// GroupMetadataRefusal
//
// A group this account left answers the w:g2 metadata IQ with 403. Baileys
// treats that the same as chat.readOnly after GROUP_PARTICIPANT_LEAVE/REMOVE.
// =============================================================================
using Unison.Baileys.Protocol;

namespace Unison.WhatsAppClient.Helpers
{
    public static class GroupMetadataRefusal
    {
        public static bool IsNoLongerParticipant(BinaryNode response)
        {
            if (response == null)
            {
                return false;
            }

            BinaryNode error = response.GetChild("error");
            if (error == null)
            {
                return false;
            }

            string code = error.GetAttribute("code");
            return code == "403" || code == "401";
        }
    }
}
