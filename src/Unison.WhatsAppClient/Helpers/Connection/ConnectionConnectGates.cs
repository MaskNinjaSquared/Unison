// =============================================================================
// ConnectionConnectGates
//
// First decisions inside ConnectAsync: skip healthy registered, force-fresh for
// QR, abort on fatal. Opening the socket stays on the host.
// =============================================================================
using System;
using Unison.Core.Contracts.WhatsApp;

namespace Unison.WhatsAppClient.Helpers
{
    public enum ConnectionConnectGate
    {
        /// <summary>Registered companion already has a healthy socket — do nothing.</summary>
        SkipAlreadyHealthy,

        /// <summary>Fatal latch is set — do not open another socket.</summary>
        AbortFatalSession,

        /// <summary>Proceed to drop/create transport (may journal QR force-fresh first).</summary>
        Proceed
    }

    /// <summary>Pure early gates for <c>ConnectAsync</c>.</summary>
    public static class ConnectionConnectGates
    {
        public static ConnectionConnectGate Decide(IConnectionConnectGateSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (source.HasHealthyRegisteredSocket)
            {
                return ConnectionConnectGate.SkipAlreadyHealthy;
            }

            if (source.ShouldRefuseConnectBecauseSessionDied)
            {
                return ConnectionConnectGate.AbortFatalSession;
            }

            return ConnectionConnectGate.Proceed;
        }

        /// <summary>
        /// Unregistered QR with a live socket: Connect must drop it before opening a new one.
        /// Journal-only; the gate itself is still <see cref="ConnectionConnectGate.Proceed"/>.
        /// </summary>
        public static bool ShouldJournalForceFreshForQr(IConnectionConnectGateSource source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            return source.HasHealthyUnregisteredSocket &&
                   !source.ShouldRefuseConnectBecauseSessionDied;
        }
    }
}
