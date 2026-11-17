// =============================================================================
// ConnectionHostDependencies
//
// DI-built connection state and wire owners the host only forwards to. Each
// member is its own DI singleton; this type just keeps the host ctor readable
// and goes away with the host.
// =============================================================================
using System;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    internal sealed class ConnectionHostDependencies
    {
        public ConnectionHostDependencies(
            ConnectionEventHub events,
            SessionEstablishedGate sessionEstablished,
            FirstSyncListHoldState firstSyncHold,
            DecryptedEventCounter decryptedEvents,
            QrCodeWireOwner qrCode,
            SocketFacadeWireOwner socketFacade,
            InboundWireNodeOwner inboundWireNode,
            DecryptedMessageWireOwner decryptedMessage,
            ReconnectHostAdapter reconnect,
            CurrentSocketHealthHost currentSocketHealth,
            HistoryCatchUpHost historyCatchUp,
            ConnectionSessionHost connectionSession,
            ChatCatalogPersistenceHost catalogPersistence,
            StartupMaintenanceHost startupMaintenance)
        {
            CatalogPersistence = catalogPersistence
                ?? throw new ArgumentNullException(nameof(catalogPersistence));
            Reconnect = reconnect ?? throw new ArgumentNullException(nameof(reconnect));
            HistoryCatchUp = historyCatchUp ?? throw new ArgumentNullException(nameof(historyCatchUp));
            ConnectionSession = connectionSession
                ?? throw new ArgumentNullException(nameof(connectionSession));
            StartupMaintenance = startupMaintenance
                ?? throw new ArgumentNullException(nameof(startupMaintenance));
            CurrentSocketHealth = currentSocketHealth
                ?? throw new ArgumentNullException(nameof(currentSocketHealth));
            Events = events ?? throw new ArgumentNullException(nameof(events));
            SessionEstablished = sessionEstablished
                ?? throw new ArgumentNullException(nameof(sessionEstablished));
            FirstSyncHold = firstSyncHold ?? throw new ArgumentNullException(nameof(firstSyncHold));
            DecryptedEvents = decryptedEvents
                ?? throw new ArgumentNullException(nameof(decryptedEvents));
            QrCode = qrCode ?? throw new ArgumentNullException(nameof(qrCode));
            SocketFacade = socketFacade ?? throw new ArgumentNullException(nameof(socketFacade));
            InboundWireNode = inboundWireNode
                ?? throw new ArgumentNullException(nameof(inboundWireNode));
            DecryptedMessage = decryptedMessage
                ?? throw new ArgumentNullException(nameof(decryptedMessage));
        }

        public ReconnectHostAdapter Reconnect { get; }

        public HistoryCatchUpHost HistoryCatchUp { get; }

        public ConnectionSessionHost ConnectionSession { get; }

        public StartupMaintenanceHost StartupMaintenance { get; }

        public ChatCatalogPersistenceHost CatalogPersistence { get; }

        public CurrentSocketHealthHost CurrentSocketHealth { get; }

        public ConnectionEventHub Events { get; }

        public SessionEstablishedGate SessionEstablished { get; }

        public FirstSyncListHoldState FirstSyncHold { get; }

        public DecryptedEventCounter DecryptedEvents { get; }

        public QrCodeWireOwner QrCode { get; }

        public SocketFacadeWireOwner SocketFacade { get; }

        public InboundWireNodeOwner InboundWireNode { get; }

        public DecryptedMessageWireOwner DecryptedMessage { get; }
    }
}
