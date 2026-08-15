// =============================================================================
// NackReason
//
// The codes we send back when we refuse a stanza.
//
// An ack with an error attribute tells the server why we could not handle the
// message, and the server changes its behaviour accordingly - a 487 stops it
// resending, a 500 does not. The current code sends a plain ack for everything,
// including messages it failed to decrypt, so the server has no idea anything
// went wrong. These are the codes that fix that.
//
// Ports: rc14 NACK_REASONS in src/Utils/decode-wa-message.ts
// =============================================================================
namespace Unison.Socket.Messages
{
    public static class NackReason
    {
        /// <summary>The sender is rate-limited by reachout rules; seen on outgoing acks from the server.</summary>
        public const int SenderReachoutTimelocked = 463;

        /// <summary>We could not read the stanza. Typically a used or missing prekey.</summary>
        public const int ParsingError = 487;

        public const int UnrecognizedStanza = 488;

        public const int UnrecognizedStanzaClass = 489;

        public const int UnrecognizedStanzaType = 490;

        public const int InvalidProtobuf = 491;

        public const int InvalidHostedCompanionStanza = 493;

        /// <summary>Sent for msmsg payloads we deliberately do not try to decrypt.</summary>
        public const int MissingMessageSecret = 495;

        public const int SignalErrorOldCounter = 496;

        public const int MessageDeletedOnPeer = 499;

        /// <summary>Anything else that went wrong on our side. The server will try again.</summary>
        public const int UnhandledError = 500;
    }
}
