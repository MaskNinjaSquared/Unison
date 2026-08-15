// =============================================================================
// PresenceUpdate
//
// Who is online, and who is typing.
//
// The wire carries these as two different stanzas - <presence> for online state
// and <chatstate> for typing - but the app only ever wants one answer per
// participant, so both collapse into the same shape here, exactly as Baileys
// does. In a group the update is keyed by participant, which is why this is a
// map and not a single value.
//
// Ports: rc14 WAPresence and PresenceData in src/Types/Chat.ts
// =============================================================================
using System.Collections.Generic;

namespace Unison.Socket.Models
{
    public enum WaPresence
    {
        Unavailable = 0,
        Available = 1,
        Composing = 2,
        Recording = 3,
        Paused = 4
    }

    public sealed class PresenceData
    {
        public WaPresence LastKnownPresence { get; set; }

        /// <summary>Unix seconds of the last time they were online, when the server discloses it.</summary>
        public long? LastSeen { get; set; }
    }

    public sealed class PresenceUpdate
    {
        public PresenceUpdate()
        {
            Presences = new Dictionary<string, PresenceData>();
        }

        /// <summary>The chat the update belongs to: a contact, or the group being typed in.</summary>
        public string Id { get; set; }

        /// <summary>Keyed by participant JID. In a one-to-one chat that is the contact itself.</summary>
        public IDictionary<string, PresenceData> Presences { get; private set; }
    }

    public static class WaPresenceParser
    {
        /// <summary>
        /// Maps a wire value onto the enum. Unknown values read as unavailable, which is the safe
        /// default: showing someone as offline when they are not is a smaller error than the reverse.
        /// </summary>
        public static WaPresence Parse(string value)
        {
            switch (value)
            {
                case "available":
                    return WaPresence.Available;
                case "composing":
                    return WaPresence.Composing;
                case "recording":
                    return WaPresence.Recording;
                case "paused":
                    return WaPresence.Paused;
                default:
                    return WaPresence.Unavailable;
            }
        }

        public static string ToWire(WaPresence presence)
        {
            switch (presence)
            {
                case WaPresence.Available:
                    return "available";
                case WaPresence.Composing:
                    return "composing";
                case WaPresence.Recording:
                    return "recording";
                case WaPresence.Paused:
                    return "paused";
                default:
                    return "unavailable";
            }
        }
    }
}
