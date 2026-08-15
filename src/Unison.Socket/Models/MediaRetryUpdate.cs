// =============================================================================
// MediaRetryUpdate
//
// The phone's answer to "I could not download this attachment, where is it now?"
//
// The answer arrives encrypted under the original message's media key, which the
// socket does not have - only the host, which stored the message, does. So this
// carries the ciphertext through untouched and lets the host decrypt it once it
// has looked the message up. That is why there is no plaintext field here.
//
// Ports: rc14 messages.media-update in src/Types/Events.ts
// =============================================================================
using Unison.Socket.Messages;

namespace Unison.Socket.Models
{
    public sealed class MediaRetryUpdate
    {
        /// <summary>Which message the answer is about.</summary>
        public MessageEnvelopeKey Key { get; set; }

        /// <summary>The re-upload result, still encrypted. Null when the server reported an error.</summary>
        public MediaRetryCiphertext Media { get; set; }

        /// <summary>Server error code when the phone could not produce the media; 0 when it did.</summary>
        public int ErrorCode { get; set; }
    }

    public sealed class MediaRetryCiphertext
    {
        public byte[] Ciphertext { get; set; }

        public byte[] Iv { get; set; }
    }
}
