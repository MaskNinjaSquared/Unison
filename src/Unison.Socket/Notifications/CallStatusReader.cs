// =============================================================================
// CallStatusReader
//
// Status of one <call> action child. A "call ended" notice arrives offer-shaped
// (is_call_ended / terminate_reason) and must end the call, never ring it.
//
// Ports: rc14 getCallStatusFromNode + meowcaller engine.onOffer ended-offer guard.
// =============================================================================
using Unison.Baileys.Protocol;
using Unison.Socket.Models;

namespace Unison.Socket.Notifications
{
    public static class CallStatusReader
    {
        public static CallStatus Read(BinaryNode info)
        {
            if (info == null)
            {
                return CallStatus.Ringing;
            }

            switch (info.Tag)
            {
                case "offer":
                case "offer_notice":
                    return IsEndedOfferNotice(info) ? CallStatus.Terminate : CallStatus.Offer;

                case "reject":
                    return CallStatus.Reject;

                case "accept":
                    return CallStatus.Accept;

                case "terminate":
                    switch (info.GetAttribute("reason"))
                    {
                        case "timeout":
                            return CallStatus.Timeout;
                        case "rejected":
                            return CallStatus.Reject;
                        case "accept":
                            return CallStatus.Accept;
                        default:
                            return CallStatus.Terminate;
                    }

                default:
                    return CallStatus.Ringing;
            }
        }

        private static bool IsEndedOfferNotice(BinaryNode info) =>
            info.GetAttribute("is_call_ended") == "1" ||
            !string.IsNullOrEmpty(info.GetAttribute("terminate_reason"));
    }
}
