// =============================================================================
// PresenceHandler
//
// Online state and typing indicators, which arrive as two unrelated stanzas and
// leave as one event.
//
// A <presence> says whether someone is around, optionally with the time they
// were last seen. A <chatstate> says what they are doing right now, and its
// meaning is in the child tag rather than an attribute. Two details are carried
// over from the reference because they are not guessable: "paused" is reported
// as plain availability, since the app has nothing to show for a typing
// indicator that stopped; and composing with media=audio is a voice note being
// recorded, which is a different indicator entirely.
//
// Ports: rc14 handlePresenceUpdate in src/Socket/chats.ts
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Models;

namespace Unison.Socket.Notifications
{
    public sealed class PresenceHandler
    {
        private readonly IWaEventBus _events;
        private readonly ISocketLog _log;

        public PresenceHandler(IWaEventBus events, ISocketLog log = null)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _events = events;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>Optional filter for JIDs the host wants nothing to do with.</summary>
        public Func<string, bool> ShouldIgnoreJid { get; set; }

        public Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return Task.FromResult(true);
            }

            var jid = node.GetAttribute("from");
            if (string.IsNullOrEmpty(jid))
            {
                return Task.FromResult(true);
            }

            var filter = ShouldIgnoreJid;
            if (filter != null && filter(jid))
            {
                return Task.FromResult(true);
            }

            var participant = node.GetAttribute("participant");
            if (string.IsNullOrEmpty(participant))
            {
                participant = jid;
            }

            var presence = node.Tag == "presence"
                ? ReadPresence(node)
                : ReadChatState(node);

            if (presence == null)
            {
                _log.Warn("[Presence] Unreadable " + node.Tag + " node from " + jid);
                return Task.FromResult(true);
            }

            var update = new PresenceUpdate { Id = jid };
            update.Presences[participant] = presence;

            return _events.EmitAsync(WaEventKind.PresenceUpdate, update);
        }

        private static PresenceData ReadPresence(BinaryNode node)
        {
            var data = new PresenceData
            {
                LastKnownPresence = node.GetAttribute("type") == "unavailable"
                    ? WaPresence.Unavailable
                    : WaPresence.Available
            };

            // "deny" means the contact hides their last seen, which is not a timestamp.
            var last = node.GetAttribute("last");
            long seen;
            if (!string.IsNullOrEmpty(last) && last != "deny" && long.TryParse(last, out seen))
            {
                data.LastSeen = seen;
            }

            return data;
        }

        private static PresenceData ReadChatState(BinaryNode node)
        {
            var children = node.GetAllChildren();
            if (children == null || children.Count == 0)
            {
                return null;
            }

            var child = children[0];
            var presence = WaPresenceParser.Parse(child.Tag);

            if (presence == WaPresence.Paused)
            {
                presence = WaPresence.Available;
            }
            else if (presence == WaPresence.Composing && child.GetAttribute("media") == "audio")
            {
                presence = WaPresence.Recording;
            }

            return new PresenceData { LastKnownPresence = presence };
        }
    }
}
