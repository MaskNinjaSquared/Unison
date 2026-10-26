// =============================================================================
// SocketTransportFailure
//
// Whether a socket OnError should schedule a persistent reconnect rather than
// treating the failure as application-level. Matches the tokens the live path
// already used (TLS 0x80072F7D, Secure Channel, keep-alive).
// =============================================================================
using System;
using System.IO;

namespace Unison.WhatsAppClient.Helpers
{
    public static class SocketTransportFailure
    {
        public static bool Matches(Exception ex, bool socketConnected, bool handshakeComplete)
        {
            if (ex is TimeoutException || ex is IOException)
            {
                return true;
            }

            if (!socketConnected || !handshakeComplete)
            {
                return true;
            }

            string message = ex?.Message;
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            // Keep Ordinal (case-sensitive) for "keep-alive" — same as the former inline check.
            return message.IndexOf("0x80072F7D", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("Secure Channel Failure", StringComparison.Ordinal) >= 0 ||
                   message.IndexOf("keep-alive", StringComparison.Ordinal) >= 0;
        }
    }
}
