// =============================================================================
// MessageDecoder
//
// Reads a message stanza's envelope: which chat it belongs to, who sent it, and
// whether we sent it ourselves.
//
// It looks like attribute shuffling and it is the source of a whole class of
// bugs. Whether a message is "from me" depends on the address space it arrived
// in, so comparing only against our phone number misfiles everything a LID chat
// sends. And a stanza from our own device with no recipient - history sync,
// app-state sync - is still from us, which is what makes the self-only protocol
// handlers run.
//
// Ports: rc14 decodeMessageNode in src/Utils/decode-wa-message.ts
// =============================================================================
using System;
using Unison.Baileys.Protocol;
using Unison.Socket.WABinary;

namespace Unison.Socket.Messages
{
    public static class MessageDecoder
    {
        /// <summary>
        /// Parses the stanza without decrypting it. Throws when the stanza cannot belong to any
        /// chat, which is a protocol error rather than a message we should try to handle.
        /// </summary>
        public static MessageEnvelope Decode(BinaryNode stanza, string meId, string meLid)
        {
            if (stanza == null)
            {
                throw new ArgumentNullException(nameof(stanza));
            }

            var messageId = stanza.GetAttribute("id");
            var from = stanza.GetAttribute("from");
            var participant = stanza.GetAttribute("participant");
            var recipient = stanza.GetAttribute("recipient");

            if (string.IsNullOrEmpty(messageId))
            {
                throw new InvalidOperationException("Invalid message stanza: missing id attribute");
            }

            if (string.IsNullOrEmpty(from))
            {
                throw new InvalidOperationException("Invalid message stanza: missing from attribute");
            }

            var addressing = AddressingContext.Extract(stanza);

            string chatId;
            string author;
            var fromMe = false;
            MessageEnvelopeKind kind;

            if (JidUtils.IsAnyPn(from) || JidUtils.IsAnyLid(from))
            {
                if (!string.IsNullOrEmpty(recipient) && !JidUtils.IsMetaAi(recipient))
                {
                    if (!IsSelf(from, meId, meLid))
                    {
                        throw new InvalidOperationException("Message carries a recipient but is not from us");
                    }

                    fromMe = true;
                    chatId = recipient;
                }
                else
                {
                    // Peer-routed stanzas addressed to ourselves carry no recipient, and are
                    // still from us.
                    fromMe = IsSelf(from, meId, meLid);
                    chatId = from;
                }

                kind = MessageEnvelopeKind.Chat;
                author = from;
            }
            else if (JidUtils.IsGroup(from))
            {
                if (string.IsNullOrEmpty(participant))
                {
                    throw new InvalidOperationException("Group message without a participant");
                }

                fromMe = IsSelf(participant, meId, meLid);
                kind = MessageEnvelopeKind.Group;
                author = participant;
                chatId = from;
            }
            else if (JidUtils.IsBroadcast(from))
            {
                if (string.IsNullOrEmpty(participant))
                {
                    throw new InvalidOperationException("Broadcast message without a participant");
                }

                var isMine = JidUtils.AreSameUser(participant, meId);
                kind = JidUtils.IsStatusBroadcast(from)
                    ? (isMine ? MessageEnvelopeKind.DirectPeerStatus : MessageEnvelopeKind.OtherStatus)
                    : (isMine ? MessageEnvelopeKind.PeerBroadcast : MessageEnvelopeKind.OtherBroadcast);

                fromMe = isMine;
                chatId = from;
                author = participant;
            }
            else if (JidUtils.IsNewsletter(from))
            {
                kind = MessageEnvelopeKind.Newsletter;
                chatId = from;
                author = from;
                fromMe = IsSelf(from, meId, meLid);
            }
            else
            {
                throw new InvalidOperationException("Unknown message type from " + from);
            }

            var isGroupChat = JidUtils.IsGroup(chatId);

            var envelope = new MessageEnvelope
            {
                Kind = kind,
                Author = author,

                // In a chat the author's own session decrypts; in a group it is the group's.
                Sender = kind == MessageEnvelopeKind.Chat ? author : chatId,
                Category = stanza.GetAttribute("category"),
                MessageTimestamp = ParseTimestamp(stanza.GetAttribute("t")),
                PushName = stanza.GetAttribute("notify"),
                Broadcast = JidUtils.IsBroadcast(from),
                Key = new MessageEnvelopeKey
                {
                    RemoteJid = chatId,
                    RemoteJidAlt = isGroupChat ? null : addressing.SenderAlt,
                    FromMe = fromMe,
                    Id = messageId,
                    Participant = participant,
                    ParticipantAlt = isGroupChat ? addressing.SenderAlt : null,
                    AddressingMode = addressing.AddressingMode,
                    ServerId = kind == MessageEnvelopeKind.Newsletter ? stanza.GetAttribute("server_id") : null
                }
            };

            return envelope;
        }

        /// <summary>True when the JID is one of our own identities, in either address space.</summary>
        private static bool IsSelf(string jid, string meId, string meLid)
        {
            return JidUtils.AreSameUser(jid, meId) ||
                   (!string.IsNullOrEmpty(meLid) && JidUtils.AreSameUser(jid, meLid));
        }

        private static long ParseTimestamp(string value)
        {
            long timestamp;
            return long.TryParse(value, out timestamp) ? timestamp : 0;
        }
    }
}
