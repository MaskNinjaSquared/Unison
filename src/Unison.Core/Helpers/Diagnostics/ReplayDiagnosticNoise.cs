// =============================================================================
// ReplayDiagnosticNoise
//
// Log lines that are useful while debugging a live socket and useless while a
// history replay is dumping thousands of them. The suppression switch stays
// with the caller; this only recognises the line.
// =============================================================================
using System;

namespace Unison.Core.Helpers
{
    public static class ReplayDiagnosticNoise
    {
        private static readonly string[] Prefixes =
        {
            "[Noise] Encrypted ",
            "[Noise] Decrypted ",
            "[Noise] Encoded frame:",
            "[Noise] Received ",
            "[Noise] Decoded frame:",
            "[Noise] Waiting for more data,",
            "[Socket] Decrypted Signal payload:",
            "[Socket] Unpadded payload:",
            "[Signal] Processing skmsg ",
            "[Signal] Processing pkmsg ",
            "[Signal] Processing msg ",
            "[Signal] No SenderKey found ",
            "[Signal] Invalid SenderKeyState ",
            "[Signal] SenderKey keyId mismatch:",
            "[Signal] SenderKey: too many iterations ahead",
            "[Signal] SenderKey decryption failed:",
            "[Signal] Successfully decrypted payload ",
            "[Signal] SenderKey old-iteration replay path:",
            "[Signal] SenderKey cache hit ",
            "[Signal] SenderKey cache miss ",
            "[Signal] Direct skipped-key cache hit ",
            "[Signal] Direct skipped-key cache miss ",
            "[Signal] Direct skipped-key cache stored "
        };

        public static bool Matches(string message)
        {
            if (string.IsNullOrEmpty(message))
            {
                return false;
            }

            for (int i = 0; i < Prefixes.Length; i++)
            {
                if (message.StartsWith(Prefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
