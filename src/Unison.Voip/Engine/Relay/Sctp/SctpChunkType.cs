namespace Unison.Voip.Engine.Relay.Sctp
{
    public static class SctpChunkType
    {
        public const byte Data = 0;
        public const byte Init = 1;
        public const byte InitAck = 2;
        public const byte Sack = 3;
        public const byte Heartbeat = 4;
        public const byte HeartbeatAck = 5;
        public const byte Abort = 6;
        public const byte Shutdown = 7;
        public const byte CookieEcho = 10;
        public const byte CookieAck = 11;
    }

    public static class SctpPayloadProtocol
    {
        /// <summary>WebRTC DCEP.</summary>
        public const uint Dcep = 50;

        /// <summary>WebRTC binary (non-empty).</summary>
        public const uint Binary = 53;

        /// <summary>WebRTC binary empty.</summary>
        public const uint BinaryEmpty = 57;
    }
}
