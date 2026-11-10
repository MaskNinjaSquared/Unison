// =============================================================================
// StatusPublishRefusedException
//
// The server answered the Status stanza with an ack error. The relay had
// already returned by then, so without this the composer reported a published
// Status that nobody ever received.
// =============================================================================
using System;

namespace Unison.Core.Contracts.WhatsApp
{
    /// <summary>
    /// Thrown when WhatsApp refuses a published Status. <see cref="ErrorCode"/> is the
    /// server's ack code; <see cref="Reason"/> is what that code means, for the log.
    /// </summary>
    public sealed class StatusPublishRefusedException : Exception
    {
        public StatusPublishRefusedException(string errorCode, string reason, string messageId = null)
            : base("WhatsApp refused the Status: error " + errorCode + " (" + reason + ")")
        {
            ErrorCode = errorCode;
            Reason = reason;
            MessageId = messageId;
        }

        /// <summary>The ack error code, for example "400" or "479".</summary>
        public string ErrorCode { get; }

        /// <summary>What that code means, in English, for diagnostics.</summary>
        public string Reason { get; }

        /// <summary>
        /// The id the Status was sent under. The local echo has already put it in "My status"
        /// by the time the refusal lands, so the caller needs it to take that copy back out.
        /// </summary>
        public string MessageId { get; }
    }
}
