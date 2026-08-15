// =============================================================================
// SyncActionProcessor
//
// Turns decoded mutations into the events the app reacts to.
//
// A mutation says almost nothing on its own: the action carries the new value
// and the index says what it applies to. Muting chat X, starring message Y and
// renaming contact Z are the same wire shape distinguished only by the first
// element of the index, which is why the dispatch is on the index and not on the
// action type.
//
// The index is [type, id, messageId, fromMe], with the last two present only for
// per-message actions. "fromMe" is the string "1", not a boolean, because it was
// a JSON array before it was anything else.
//
// Ports: rc14 processSyncAction in src/Utils/process-message.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;
using Unison.Socket.Messages;
using Unison.Socket.Models;

namespace Unison.Socket.AppState
{
    public sealed class SyncActionProcessor
    {
        private readonly IWaEventBus _events;
        private readonly ISocketLog _log;

        public SyncActionProcessor(IWaEventBus events, ISocketLog log = null)
        {
            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _events = events;
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Called when the phone tells us our own display name changed. It lives on the
        /// credentials rather than in any collection, so the host has to persist it.
        /// </summary>
        public Func<string, Task> PushNameChanged { get; set; }

        /// <summary>
        /// Applies a batch. Chat updates are gathered and emitted together, because a sync
        /// commonly touches dozens of chats and the app should redraw once.
        /// </summary>
        public async Task ProcessAsync(IEnumerable<ChatMutation> mutations)
        {
            if (mutations == null)
            {
                return;
            }

            var chats = new List<ChatUpdate>();
            var contacts = new List<ContactUpdate>();
            var messages = new List<MessageUpdate>();
            var deletedMessages = new List<MessageEnvelopeKey>();
            var deletedChats = new List<string>();

            foreach (var mutation in mutations)
            {
                if (mutation == null || mutation.SyncAction == null)
                {
                    continue;
                }

                try
                {
                    Apply(mutation, chats, contacts, messages, deletedMessages, deletedChats);
                }
                catch (Exception ex)
                {
                    _log.Error("[AppState] Failed to apply a " + mutation.Action + " mutation", ex);
                }
            }

            if (chats.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.ChatsUpdate, chats).ConfigureAwait(false);
            }

            if (contacts.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.ContactsUpsert, contacts).ConfigureAwait(false);
            }

            if (messages.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.MessagesUpdate, messages).ConfigureAwait(false);
            }

            if (deletedMessages.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.MessagesDelete, deletedMessages).ConfigureAwait(false);
            }

            if (deletedChats.Count > 0)
            {
                await _events.EmitAsync(WaEventKind.ChatsDelete, deletedChats).ConfigureAwait(false);
            }
        }

        private void Apply(
            ChatMutation mutation,
            ICollection<ChatUpdate> chats,
            ICollection<ContactUpdate> contacts,
            ICollection<MessageUpdate> messages,
            ICollection<MessageEnvelopeKey> deletedMessages,
            ICollection<string> deletedChats)
        {
            var value = mutation.SyncAction.Value;
            if (value == null)
            {
                return;
            }

            // Account settings have no chat in the index (["setting_pushName"]).
            // Requiring Index[1] first dropped the user's own display name on every login.
            if (value.PushNameSetting != null)
            {
                ApplyPushNameSetting(value.PushNameSetting.Name);
                return;
            }

            var id = mutation.Index.Count > 1 ? mutation.Index[1] : null;
            if (string.IsNullOrEmpty(id))
            {
                return;
            }

            if (value.MuteAction != null)
            {
                chats.Add(new ChatUpdate(id)
                {
                    MuteEndTime = value.MuteAction.Muted
                        ? unchecked((long)value.MuteAction.MuteEndTimestamp)
                        : 0
                });
                return;
            }

            if (value.ArchiveChatAction != null)
            {
                chats.Add(new ChatUpdate(id) { Archived = value.ArchiveChatAction.Archived });
                return;
            }

            if (value.PinAction != null)
            {
                // A pin is ordered by when it was pinned, so the sort key is the mutation's own
                // timestamp rather than anything in the action.
                chats.Add(new ChatUpdate(id)
                {
                    Pinned = value.PinAction.Pinned ? value.Timestamp : 0
                });
                return;
            }

            if (value.MarkChatAsReadAction != null)
            {
                // Marking unread is expressed as a negative count upstream: the true number is not
                // known here, only that there is something to see.
                chats.Add(new ChatUpdate(id)
                {
                    UnreadCount = value.MarkChatAsReadAction.Read ? 0 : -1,
                    MarkedAsUnread = !value.MarkChatAsReadAction.Read
                });
                return;
            }

            if (value.ClearChatAction != null)
            {
                chats.Add(new ChatUpdate(id) { ConversationTimestamp = 0 });
                return;
            }

            if (value.DeleteChatAction != null)
            {
                deletedChats.Add(id);
                return;
            }

            if (value.ContactAction != null)
            {
                // The index is normally the phone number and the action names the LID beside it.
                // Both are carried so the host can tie the two addresses together: a conversation
                // that arrives under a LID has no name of its own, and this is where the name it
                // was saved under becomes reachable.
                var contact = value.ContactAction;
                contacts.Add(new ContactUpdate(id)
                {
                    Name = FirstNonEmpty(contact.FullName, contact.FirstName, contact.Username),
                    Lid = contact.LidJid,
                    PhoneNumber = string.IsNullOrEmpty(contact.PnJid) ? null : contact.PnJid
                });
                return;
            }

            if (value.LidContactAction != null)
            {
                // Same thing for an account the user only ever saw as a LID: here the index is
                // the LID itself, so there is no number to pair it with.
                var contact = value.LidContactAction;
                contacts.Add(new ContactUpdate(id)
                {
                    Name = FirstNonEmpty(contact.FullName, contact.FirstName, contact.Username),
                    Lid = id
                });
                return;
            }

            if (value.StarAction != null)
            {
                var key = BuildMessageKey(mutation, id);
                if (key != null)
                {
                    messages.Add(new MessageUpdate
                    {
                        RemoteJid = key.RemoteJid,
                        MessageId = key.Id,
                        FromMe = key.FromMe,
                        Starred = value.StarAction.Starred
                    });
                }

                return;
            }

            if (value.DeleteMessageForMeAction != null)
            {
                var key = BuildMessageKey(mutation, id);
                if (key != null)
                {
                    deletedMessages.Add(key);
                }

                return;
            }

            _log.Debug("[AppState] Unhandled action " + mutation.Action + " for " + id);
        }

        /// <summary>
        /// The account's own push name. The index is only the setting name, so this must run
        /// before chat-id is required.
        /// </summary>
        private void ApplyPushNameSetting(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            var changed = PushNameChanged;
            if (changed == null)
            {
                return;
            }

            _log.Debug("[AppState] Push name setting: '" + name + "'");
            var work = changed(name);
            if (work != null)
            {
                work.ContinueWith(
                    t => _log.Error("[AppState] Failed to store the new push name", t.Exception),
                    TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        /// <summary>
        /// A contact can be saved under a full name, a first name only, or nothing but a
        /// username. The first one present is the best name available.
        /// </summary>
        private static string FirstNonEmpty(params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// Per-message actions carry the message id and direction in the index, since the action
        /// itself only says what happened, not to what.
        /// </summary>
        private static MessageEnvelopeKey BuildMessageKey(ChatMutation mutation, string remoteJid)
        {
            if (mutation.Index.Count < 3)
            {
                return null;
            }

            return new MessageEnvelopeKey
            {
                RemoteJid = remoteJid,
                Id = mutation.Index[2],
                FromMe = mutation.Index.Count > 3 && mutation.Index[3] == "1"
            };
        }
    }
}
