// =============================================================================
// AdvConstants
//
// Domain-separation prefixes for the ADV (Auxiliary Device Verification)
// signatures exchanged during pairing. Each signature covers a different
// message, and the prefix is what stops one from being replayed as another.
//
// Ports: rc14 WA_ADV_*_SIG_PREFIX in src/Defaults/index.ts
// =============================================================================
namespace Unison.Socket.Session.Pairing
{
    internal static class AdvConstants
    {
        public static readonly byte[] AccountSigPrefix = { 6, 0 };

        public static readonly byte[] DeviceSigPrefix = { 6, 1 };

        /// <summary>
        /// Used instead of <see cref="AccountSigPrefix"/> for hosted (business) accounts.
        /// Absent from the legacy pairing code, which fails to link hosted accounts.
        /// </summary>
        public static readonly byte[] HostedAccountSigPrefix = { 6, 5 };
    }
}
