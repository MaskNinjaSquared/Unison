// =============================================================================
// AddressingContext
//
// Works out which address space a stanza is written in, and what the sender's
// other identity is.
//
// A message can arrive addressed by LID or by phone number, and the server
// helpfully includes the counterpart in an attribute whose name depends on which
// way round it is - participant_pn and sender_pn one way, participant_lid and
// sender_lid the other. The current code checks a handful of those attributes in
// several places and disagrees with itself about the precedence; this reads them
// once, in the Baileys order.
//
// Ports: rc14 extractAddressingContext in src/Utils/decode-wa-message.ts
// =============================================================================
using Unison.Baileys.Protocol;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public sealed class AddressingContext
    {
        /// <summary>"lid" or "pn": the space the stanza's own JIDs are written in.</summary>
        public string AddressingMode { get; set; }

        /// <summary>The sender's identity in the other space, when the server disclosed it.</summary>
        public string SenderAlt { get; set; }

        /// <summary>The recipient's identity in the other space.</summary>
        public string RecipientAlt { get; set; }

        public bool IsLidAddressed
        {
            get { return AddressingMode == "lid"; }
        }

        public static AddressingContext Extract(BinaryNode stanza)
        {
            if (stanza == null)
            {
                return new AddressingContext { AddressingMode = "pn" };
            }

            var sender = stanza.GetAttribute("participant");
            if (string.IsNullOrEmpty(sender))
            {
                sender = stanza.GetAttribute("from");
            }

            var mode = stanza.GetAttribute("addressing_mode");
            if (string.IsNullOrEmpty(mode))
            {
                mode = JidUtils.IsAnyLid(sender) ? "lid" : "pn";
            }

            if (mode == "lid")
            {
                return new AddressingContext
                {
                    AddressingMode = mode,
                    SenderAlt = FirstNonEmpty(
                        stanza.GetAttribute("participant_pn"),
                        stanza.GetAttribute("sender_pn"),
                        stanza.GetAttribute("peer_recipient_pn")),
                    RecipientAlt = stanza.GetAttribute("recipient_pn")
                };
            }

            return new AddressingContext
            {
                AddressingMode = mode,
                SenderAlt = FirstNonEmpty(
                    stanza.GetAttribute("participant_lid"),
                    stanza.GetAttribute("sender_lid"),
                    stanza.GetAttribute("peer_recipient_lid")),
                RecipientAlt = stanza.GetAttribute("recipient_lid")
            };
        }

        /// <summary>
        /// The LID/PN pair this stanza discloses, or null when it names only one space.
        /// Every incoming stanza is a chance to learn a mapping for free.
        /// </summary>
        public Signal.LidMapping ToMapping(string sender)
        {
            if (string.IsNullOrEmpty(sender) || string.IsNullOrEmpty(SenderAlt))
            {
                return null;
            }

            if (IsLidAddressed)
            {
                return JidUtils.IsAnyLid(sender) && JidUtils.IsAnyPn(SenderAlt)
                    ? new Signal.LidMapping(sender, SenderAlt)
                    : null;
            }

            return JidUtils.IsAnyPn(sender) && JidUtils.IsAnyLid(SenderAlt)
                ? new Signal.LidMapping(SenderAlt, sender)
                : null;
        }

        private static string FirstNonEmpty(string a, string b, string c)
        {
            if (!string.IsNullOrEmpty(a))
            {
                return a;
            }

            return !string.IsNullOrEmpty(b) ? b : c;
        }
    }
}
