using System;

namespace Unison.Core.Models
{
    /// <summary>
    /// Presence / chatstate update for a contact JID (online, typing, last seen, …).
    /// Raised by the connection client; ViewModels format UI text without touching SocketClient.
    /// </summary>
    public class PresenceUpdateEventArgs : EventArgs
    {
        public string Jid { get; set; }
        public string Presence { get; set; }
        public long? LastSeen { get; set; }
    }
}
