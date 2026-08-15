// =============================================================================
// NotificationHandler
//
// Everything the server tells us that is not a message, a receipt or a call.
//
// Group membership, avatar changes, privacy tokens, our dwindling pre-key
// supply, and the nudge that says an app-state collection moved on: they all
// arrive as <notification> stanzas distinguished only by a type attribute. Until
// now this layer acked them and threw them away, which is why the new stack
// could receive messages but never noticed a rename.
//
// Two things are worth knowing. The ack is sent whatever happens, including
// after a handler throws - an unacked notification is redelivered forever, so
// dropping one is better than looping on it. And nothing here fetches: the
// handler translates and publishes, and the host decides whether a changed
// avatar is worth a round trip.
//
// Ports: rc14 processNotification and handleEncryptNotification in
// src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Models;
using Unison.Socket.UseCases.Messages;
using Unison.Socket.WABinary;

namespace Unison.Socket.Notifications
{
    public sealed class NotificationHandler
    {
        /// <summary>
        /// Below this many unused pre-keys the server starts turning people away when they try to
        /// start a conversation with us, so it is a refill threshold rather than a warning.
        /// </summary>
        public const int MinPreKeyCount = 5;

        private readonly IWaEventBus _events;
        private readonly SendMessageAckUseCase _ack;
        private readonly Func<string> _meId;
        private readonly ISocketLog _log;

        public NotificationHandler(
            IWaEventBus events,
            SendMessageAckUseCase ack,
            Func<string> meId,
            ISocketLog log = null)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            if (ack == null)
            {
                throw new ArgumentNullException(nameof(ack));
            }

            _events = events;
            _ack = ack;
            _meId = meId ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>Called when the server says our pre-key supply is running low.</summary>
        public Func<Task> UploadPreKeys { get; set; }

        /// <summary>Called with the collection name the server says has moved on.</summary>
        public Func<string, Task> ResyncAppState { get; set; }

        /// <summary>
        /// Called when a contact's identity key changed. The session with them is no longer valid
        /// and has to be torn down, or every message we send will be unreadable.
        /// </summary>
        public Func<string, Task> IdentityChanged { get; set; }

        /// <summary>Called when the account's default disappearing-message duration changed.</summary>
        public Func<int, Task> DisappearingModeChanged { get; set; }

        /// <summary>
        /// Called with the id of a group whose settings changed. The send path caches group
        /// metadata, and an ephemeral timer or an announce flag it read before the change is
        /// stale from here on.
        /// </summary>
        public Action<string> GroupSettingsChanged { get; set; }

        /// <summary>
        /// Called when a group gained or lost members. Beyond invalidating a cache this decides
        /// who the next message's sender key has to reach.
        /// </summary>
        public Action<GroupParticipantsUpdate> GroupParticipantsChanged { get; set; }

        /// <summary>
        /// Called with a media retry answer before it is published. The media layer waits on
        /// these to finish a refresh it asked for; the event still goes out either way, since the
        /// phone also sends them unprompted.
        /// </summary>
        public Action<MediaRetryUpdate> MediaRetryReceived { get; set; }

        public async Task HandleAsync(BinaryNode node)
        {
            if (node == null)
            {
                return;
            }

            try
            {
                await DispatchAsync(node).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(
                    "[Notification] Failed to handle a " + node.GetAttribute("type") + " notification",
                    ex);
            }
            finally
            {
                await _ack.ExecuteAsync(node).ConfigureAwait(false);
            }
        }

        private Task DispatchAsync(BinaryNode node)
        {
            var type = node.GetAttribute("type");
            var children = node.GetAllChildren();
            var child = children != null && children.Count > 0 ? children[0] : null;

            switch (type)
            {
                case "w:gp2":
                    return HandleGroupAsync(node);

                case "encrypt":
                    return HandleEncryptAsync(node);

                case "server_sync":
                    return HandleServerSyncAsync(node);

                case "account_sync":
                    return HandleAccountSyncAsync(child);

                case "privacy_token":
                    return HandlePrivacyTokenAsync(child);

                case "picture":
                    return HandlePictureAsync(node);

                case "mediaretry":
                    return HandleMediaRetryAsync(node);

                case "devices":
                    // Device lists are refreshed on demand by the send path, which is the only
                    // caller that needs them, so there is nothing to do but note it.
                    _log.Debug("[Notification] Device list changed for " + node.GetAttribute("from"));
                    return Done;

                default:
                    _log.Debug("[Notification] Ignoring type=" + type);
                    return Done;
            }
        }

        private async Task HandleGroupAsync(BinaryNode node)
        {
            var parsed = GroupNotificationParser.Parse(node);
            if (parsed.IsEmpty)
            {
                return;
            }

            if (parsed.Created != null)
            {
                await _events.EmitAsync(
                    WaEventKind.GroupsUpsert,
                    new List<GroupMetadata> { parsed.Created }).ConfigureAwait(false);

                await _events.EmitAsync(
                    WaEventKind.ChatsUpsert,
                    new List<ChatUpdate>
                    {
                        new ChatUpdate(parsed.Created.Id)
                        {
                            Name = parsed.Created.Subject,
                            ConversationTimestamp = parsed.Created.Creation
                        }
                    }).ConfigureAwait(false);
            }

            if (parsed.Update != null)
            {
                Notify(GroupSettingsChanged, parsed.Update.Id);

                await _events.EmitAsync(
                    WaEventKind.GroupsUpdate,
                    new List<GroupUpdate> { parsed.Update }).ConfigureAwait(false);

                // The disappearing-message setting lives on the chat as well as on the group, and
                // the app reads it from the chat when deciding what to stamp on a message.
                if (parsed.Update.EphemeralDuration.HasValue)
                {
                    await _events.EmitAsync(
                        WaEventKind.ChatsUpdate,
                        new List<ChatUpdate>
                        {
                            new ChatUpdate(parsed.Update.Id)
                            {
                                EphemeralExpiration = parsed.Update.EphemeralDuration
                            }
                        }).ConfigureAwait(false);
                }
            }

            if (parsed.Participants != null)
            {
                Notify(GroupParticipantsChanged, parsed.Participants);

                await _events.EmitAsync(
                    WaEventKind.GroupParticipantsUpdate,
                    parsed.Participants).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Runs a host callback without letting it interrupt the notification, which still has to
        /// reach the event bus and be acked.
        /// </summary>
        private void Notify<T>(Action<T> callback, T argument)
        {
            if (callback == null)
            {
                return;
            }

            try
            {
                callback(argument);
            }
            catch (Exception ex)
            {
                _log.Warn("[Notification] A group change callback threw", ex);
            }
        }

        /// <summary>
        /// From the server itself this is the pre-key count; from anyone else it is a warning that
        /// their identity key changed, which invalidates the session we hold with them.
        /// </summary>
        private async Task HandleEncryptAsync(BinaryNode node)
        {
            var from = node.GetAttribute("from");

            if (from == JidUtils.ServerWhatsApp)
            {
                var countNode = node.GetChild("count");
                if (countNode == null)
                {
                    return;
                }

                int count;
                if (!int.TryParse(countNode.GetAttribute("value"), out count))
                {
                    return;
                }

                _log.Debug("[Notification] Server reports " + count + " pre-key(s) left");

                if (count >= MinPreKeyCount)
                {
                    return;
                }

                var upload = UploadPreKeys;
                if (upload != null)
                {
                    await upload().ConfigureAwait(false);
                }

                return;
            }

            if (node.GetChild("identity") == null)
            {
                _log.Debug("[Notification] Unrecognised encrypt notification from " + from);
                return;
            }

            _log.Info("[Notification] Identity changed for " + from);

            var identityChanged = IdentityChanged;
            if (identityChanged != null)
            {
                await identityChanged(from).ConfigureAwait(false);
            }
        }

        private async Task HandleServerSyncAsync(BinaryNode node)
        {
            var resync = ResyncAppState;
            if (resync == null)
            {
                return;
            }

            foreach (var collection in node.FindAllDescendants("collection"))
            {
                var name = collection.GetAttribute("name");
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                _log.Debug("[Notification] Collection " + name + " moved on");
                await resync(name).ConfigureAwait(false);
            }
        }

        private async Task HandleAccountSyncAsync(BinaryNode child)
        {
            if (child == null)
            {
                return;
            }

            switch (child.Tag)
            {
                case "disappearing_mode":
                    int duration;
                    if (!int.TryParse(child.GetAttribute("duration"), out duration))
                    {
                        return;
                    }

                    var changed = DisappearingModeChanged;
                    if (changed != null)
                    {
                        await changed(duration).ConfigureAwait(false);
                    }

                    break;

                case "blocklist":
                    await EmitBlocklistAsync(child).ConfigureAwait(false);
                    break;
            }
        }

        private async Task EmitBlocklistAsync(BinaryNode child)
        {
            var added = new BlocklistUpdate { Action = BlocklistAction.Add };
            var removed = new BlocklistUpdate { Action = BlocklistAction.Remove };

            foreach (var item in child.GetChildren("item"))
            {
                var jid = item.GetAttribute("jid");
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }

                if (item.GetAttribute("action") == "block")
                {
                    added.Jids.Add(jid);
                }
                else
                {
                    removed.Jids.Add(jid);
                }
            }

            if (added.Jids.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.BlocklistUpdate, added).ConfigureAwait(false);
            }

            if (removed.Jids.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.BlocklistUpdate, removed).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// The token is opaque and has to be echoed back when messaging that contact, so it travels
        /// as bytes and is never interpreted here.
        /// </summary>
        private async Task HandlePrivacyTokenAsync(BinaryNode child)
        {
            if (child == null)
            {
                return;
            }

            var updates = new List<ChatUpdate>();

            foreach (var token in child.GetChildren("token"))
            {
                var jid = token.GetAttribute("jid");
                if (string.IsNullOrEmpty(jid))
                {
                    continue;
                }

                updates.Add(new ChatUpdate(jid)
                {
                    TcToken = token.GetContentBytes(),
                    TcTokenTimestamp = ParseLong(token.GetAttribute("t")),
                    TcTokenSenderTimestamp = ParseLong(token.GetAttribute("sender_t"))
                });
            }

            if (updates.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.ChatsUpdate, updates).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// A picture notification says only that the avatar moved, never what it is now. The
        /// sentinel tells the host to refetch if and when it wants to.
        /// </summary>
        private async Task HandlePictureAsync(BinaryNode node)
        {
            var from = JidUtils.NormalizedUser(node.GetAttribute("from"));
            if (string.IsNullOrEmpty(from))
            {
                return;
            }

            var state = node.GetChild("set") != null
                ? ContactImageState.Changed
                : ContactImageState.Removed;

            if (JidUtils.IsGroup(from))
            {
                await _events.EmitAsync(
                    WaEventKind.GroupsUpdate,
                    new List<GroupUpdate>
                    {
                        new GroupUpdate(from) { Author = node.GetAttribute("participant") }
                    }).ConfigureAwait(false);
            }

            await _events.EmitAsync(
                WaEventKind.ContactsUpdate,
                new List<ContactUpdate> { new ContactUpdate(from) { ImgUrl = state } }).ConfigureAwait(false);
        }

        private async Task HandleMediaRetryAsync(BinaryNode node)
        {
            var update = MediaRetryNode.Decode(node);
            if (update == null)
            {
                return;
            }

            var received = MediaRetryReceived;
            if (received != null)
            {
                received(update);
            }

            await _events.EmitAsync(
                WaEventKind.MessagesMediaUpdate,
                new List<MediaRetryUpdate> { update }).ConfigureAwait(false);
        }

        private static long? ParseLong(string value)
        {
            long parsed;
            return long.TryParse(value, out parsed) ? (long?)parsed : null;
        }

        private static Task Done
        {
            get { return Task.FromResult(true); }
        }
    }
}
