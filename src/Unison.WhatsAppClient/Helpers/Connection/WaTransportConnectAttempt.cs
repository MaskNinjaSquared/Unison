// =============================================================================
// WaTransportConnectAttempt
//
// Host-side plan: which transports to build for one ConnectAsync, in order.
// SocketBridge only receives the instances — it does not choose or construct them.
// =============================================================================
using System;
using Unison.Core.Contracts;
using Unison.Core.Helpers;

namespace Unison.WhatsAppClient.Helpers
{
    /// <summary>Primary (+ optional fallback) transports for one connect attempt.</summary>
    public sealed class WaTransportConnectAttempt
    {
        public WaTransportConnectAttempt(IWhatsAppTransport primary, IWhatsAppTransport fallback)
        {
            Primary = primary ?? throw new ArgumentNullException(nameof(primary));
            Fallback = fallback;
        }

        public IWhatsAppTransport Primary { get; }

        /// <summary>Tried only when primary connect throws. May be null.</summary>
        public IWhatsAppTransport Fallback { get; }

        public bool HasFallback => Fallback != null;

        /// <summary>
        /// Builds the pair from the host factory and the Mobile / experiment order rule.
        /// </summary>
        public static WaTransportConnectAttempt Create(
            IWhatsAppTransportFactory factory,
            bool isMobile,
            bool preferStreamOnMobile)
        {
            if (factory == null)
            {
                throw new ArgumentNullException(nameof(factory));
            }

            bool streamFirst = WaTransportConnectOrder.PreferStreamSocketFirst(
                isMobile,
                preferStreamOnMobile);

            if (streamFirst)
            {
                return new WaTransportConnectAttempt(
                    factory.CreateStreamSocketTransport(),
                    factory.CreateMessageWebSocketTransport());
            }

            return new WaTransportConnectAttempt(
                factory.CreateMessageWebSocketTransport(),
                fallback: null);
        }

        /// <summary>Uses <see cref="ConnectionStabilityExperiment.PreferStreamSocketOnMobile"/>.</summary>
        public static WaTransportConnectAttempt Create(
            IWhatsAppTransportFactory factory,
            bool isMobile)
        {
            return Create(
                factory,
                isMobile,
                ConnectionStabilityExperiment.PreferStreamSocketOnMobile);
        }
    }
}
