// =============================================================================
// CallHandler
//
// Incoming calls, and the several ways they end.
//
// Only the first stanza of a call describes it. The one that ends it says the
// call is over and nothing else, so whether the user just missed a video call or
// a voice call is knowledge that has to be carried forward from the offer - that
// is what the cache is for, and why it is keyed by call id rather than by
// caller. Entries expire because a call that was never answered and never
// terminated should not be remembered forever.
//
// Ports: rc14 handleCall and getCallStatusFromNode in src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Models;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.Utils;

namespace Unison.Socket.Notifications
{
    public sealed class CallHandler
    {
        private readonly IWaEventBus _events;
        private readonly SendMessageAckUseCase _ack;
        private readonly TtlCache<CallOffer> _offers = new TtlCache<CallOffer>(TimeSpan.FromMinutes(5));
        private readonly ISocketLog _log;

        public CallHandler(IWaEventBus events, SendMessageAckUseCase ack, ISocketLog log = null)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            _events = events;
            _ack = ack;
            _log = log ?? NullSocketLog.Instance;
        }

        public async Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            try
            {
                var call = Read(node);
                if (call != null)
                {
                    await _events.EmitAsync(
                        WaEventKind.Call,
                        new List<CallOffer> { call }).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _log.Error("[Call] Failed to handle a call node", ex);
            }
            finally
            {
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
            }
        }

        private CallOffer Read(BinaryNode node)
        {
            var children = node.GetAllChildren();
            if (children == null || children.Count == 0)
            {
                return null;
            }

            var info = children[0];
            var status = ReadStatus(info);

            var from = info.GetAttribute("from");
            if (string.IsNullOrEmpty(from))
            {
                from = info.GetAttribute("call-creator");
            }

            var call = new CallOffer
            {
                Id = info.GetAttribute("call-id"),
                From = from,
                ChatId = node.GetAttribute("from"),
                Status = status,
                Date = ParseLong(node.GetAttribute("t")),
                Offline = !string.IsNullOrEmpty(node.GetAttribute("offline"))
            };

            if (status == CallStatus.Offer)
            {
                call.IsVideo = info.GetChild("video") != null;
                call.GroupJid = info.GetAttribute("group-jid");
                call.IsGroup = info.GetAttribute("type") == "group" || !string.IsNullOrEmpty(call.GroupJid);

                if (!string.IsNullOrEmpty(call.Id))
                {
                    _offers.Set(call.Id, call);
                }

                return call;
            }

            // Carry the media type forward from the offer, which is the only stanza that stated it.
            CallOffer offer;
            if (!string.IsNullOrEmpty(call.Id) && _offers.TryGet(call.Id, out offer) && offer != null)
            {
                call.IsVideo = offer.IsVideo;
                call.IsGroup = offer.IsGroup;
                call.GroupJid = offer.GroupJid;
            }

            if (!string.IsNullOrEmpty(call.Id) && status != CallStatus.Ringing)
            {
                _offers.Remove(call.Id);
            }

            return call;
        }

        /// <summary>
        /// A terminate says why in an attribute, and the reason is what separates a call the user
        /// rejected from one they never heard ring.
        /// </summary>
        private static CallStatus ReadStatus(BinaryNode info)
        {
            switch (info.Tag)
            {
                case "offer":
                case "offer_notice":
                    return CallStatus.Offer;

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

        private static long ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? parsed : 0;
        }
    }
}
