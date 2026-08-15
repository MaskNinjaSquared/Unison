// =============================================================================
// SendPresenceUseCase
//
// Says whether we are online, and whether we are typing.
//
// These are two different stanzas that look like one feature. Being online is
// account-wide and goes out as a presence with our name on it; typing is per
// chat and goes out as a chatstate. Sending the wrong one tells the server
// something it will not correct us on, so the split is kept explicit.
//
// Subscribing is the third piece and the one that is easy to forget: the server
// sends no presence for a contact until we ask for theirs, so a chat that never
// subscribes shows everyone as permanently offline.
//
// Ports: rc14 presenceSubscribe and sendPresenceUpdate in src/Socket/chats.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Session;
using Unison.Socket.WABinary;

namespace Unison.Socket.UseCases.Chats
{
    /// <summary>What we are telling the other side we are doing.</summary>
    public enum PresenceState
    {
        Available,
        Unavailable,
        Composing,
        Recording,
        Paused
    }

    public sealed class SendPresenceUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly Func<string> _meName;

        public SendPresenceUseCase(
            ConnectionHandler connection,
            Func<string> meId,
            Func<string> meLid = null,
            Func<string> meName = null)
        {
            if (connection == null)
            {
                throw new ArgumentNullException(nameof(connection));
            }

            _connection = connection;
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _meName = meName ?? (() => null);
        }

        /// <summary>
        /// Asks to be told about someone's presence. The token is required by contacts who only
        /// share it with people they have talked to.
        /// </summary>
        public Task SubscribeAsync(string jid, byte[] trustedContactToken = null)
        {
            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("jid is required", nameof(jid));
            }

            var children = trustedContactToken != null && trustedContactToken.Length > 0
                ? new List<BinaryNode> { new BinaryNode("tctoken", null, trustedContactToken) }
                : null;

            return _connection.SendNodeAsync(new BinaryNode(
                "presence",
                new Dictionary<string, string>
                {
                    { "to", jid },
                    { "id", MessageTag() },
                    { "type", "subscribe" }
                },
                children));
        }

        /// <param name="jid">
        /// The chat being typed in. Ignored for available and unavailable, which are not about a
        /// chat at all.
        /// </param>
        public Task ExecuteAsync(PresenceState state, string jid = null)
        {
            if (state == PresenceState.Available || state == PresenceState.Unavailable)
            {
                return SendAvailabilityAsync(state);
            }

            if (string.IsNullOrEmpty(jid))
            {
                throw new ArgumentException("A chat is required to report typing", nameof(jid));
            }

            return SendChatStateAsync(state, jid);
        }

        private Task SendAvailabilityAsync(PresenceState state)
        {
            var name = _meName();
            if (string.IsNullOrEmpty(name))
            {
                // The server rejects an unnamed presence, and there is nothing to be done about it
                // here: the name arrives with the credentials once the phone has shared them.
                return Task.FromResult(true);
            }

            return _connection.SendNodeAsync(new BinaryNode(
                "presence",
                new Dictionary<string, string>
                {
                    { "name", name.Replace("@", string.Empty) },
                    { "type", state == PresenceState.Available ? "available" : "unavailable" }
                }));
        }

        private Task SendChatStateAsync(PresenceState state, string jid)
        {
            // Recording is composing with a marker on it, not a state of its own.
            var isRecording = state == PresenceState.Recording;
            var tag = isRecording || state == PresenceState.Composing ? "composing" : "paused";

            var attributes = isRecording
                ? new Dictionary<string, string> { { "media", "audio" } }
                : null;

            var from = JidUtils.GetServer(jid) == JidUtils.ServerLid ? _meLid() : _meId();

            return _connection.SendNodeAsync(new BinaryNode(
                "chatstate",
                new Dictionary<string, string>
                {
                    { "from", from },
                    { "to", jid }
                },
                new List<BinaryNode> { new BinaryNode(tag, attributes) }));
        }

        private static string MessageTag()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        }
    }
}
