// =============================================================================
// WaConnectionException
//
// A connection failure that carries the WhatsApp stream code alongside the
// message, so callers can branch on the reason instead of parsing text. This is
// the C# stand-in for the Boom errors rc14 throws with a statusCode.
//
// Ports: rc14 Boom errors carrying a DisconnectReason statusCode
// =============================================================================
using System;

namespace Unison.Socket.Session
{
    /// <summary>
    /// Connection-level failure carrying the WhatsApp stream code, so the host can decide
    /// whether to retry, re-pair or wipe the session.
    /// </summary>
    public sealed class WaConnectionException : Exception
    {
        public WaConnectionException(string message, DisconnectReason reason, Exception inner = null)
            : base(message, inner)
        {
            Reason = reason;
        }

        public DisconnectReason Reason { get; }
    }
}
