// =============================================================================
// PairingRestartStreamCode
//
// Stream code 515 after pair-success: expected close; start pairing stage-2
// reconnect rather than treating it as a fatal logout.
// =============================================================================
using System;

namespace Unison.WhatsAppClient.Helpers
{
    public static class PairingRestartStreamCode
    {
        public const string Code = "515";

        public static bool Matches(string code)
        {
            return string.Equals(code, Code, StringComparison.Ordinal);
        }
    }
}
