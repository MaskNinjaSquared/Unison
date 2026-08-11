namespace Unison.Background
{
    internal static class SocketBrokerConstants
    {
        public const string TaskName = "Unison WhatsApp Socket Activity";
        public const string TaskEntryPoint = "Unison.Background.WhatsAppSocketActivityTask";
        public const string LegacySocketId = "UnisonWhatsAppSocketV67";
        public const string RegressionInProcessTaskName =
            "Unison WhatsApp Socket Activity InProcess v2";
        public const string RegressionInProcessSocketId =
            "UnisonWhatsAppSocketV672";
        public const string SocketIdPrefix = "UnisonWhatsAppSocketV673-";
        public const string BrokerFramePrefix = "broker-frame-";
        public const string BrokerFrameExtension = ".bin";
        public const string BrokerFrameCorruptExtension = ".corrupt";
        public const string BrokerFrameAckFile = "broker-frame-ack-v2.txt";
        public const string BrokerLogFile = "socket-broker.log";
        public const string OwnershipStateFile = "socket-broker-ownership-v673.json";
        public const string OwnershipLockFile = "socket-broker-ownership-v673.lock";
        public const string ReconnectRequestFile = "socket-broker-reconnect-v673.json";
        public const int MaximumWebSocketMessageBytes = 16 * 1024 * 1024;
        public const int MaximumJournalPayloadBytes =
            MaximumWebSocketMessageBytes + (64 * 1024);
        public const int JournalEnvelopeVersion = 2;
        public const int OwnershipStateVersion = 1;
    }
}
