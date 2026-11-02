// =============================================================================
// SocketBridgeRemoteHooks
//
// Wires SocketBridge app-state / contact callbacks onto the façades. Called from
// UwpConnectionSocketSessionFactory so connect-open construction stays in one adapter.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unison.Core.Contracts.WhatsApp;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Chats;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    internal static class SocketBridgeRemoteHooks
    {
        public static void Attach(
            SocketBridge bridge,
            IChatService chatFacade,
            IContactService contacts,
            IMessageService messages,
            IGroupService groups,
            Func<string, string, Task<global::Proto.Message>> resolveSentMessage)
        {
            if (bridge == null)
            {
                throw new ArgumentNullException(nameof(bridge));
            }

            // ChatUpdate lives in Socket; Core's IChatService cannot name it. The concrete
            // ChatFacade owns the apply — same pattern as other socket→façade bridges that
            // still carry Socket model types.
            bridge.ChatSettingsChanged = update =>
                chatFacade is ChatFacade chats
                    ? chats.ApplyRemoteChatSettingsAsync(update)
                    : Task.CompletedTask;
            bridge.ContactChanged = contact =>
                contacts != null
                    ? contacts.ApplyRemoteContactUpdatesAsync(AccountContactUpdateMap.From(contact))
                    : Task.CompletedTask;
            bridge.ContactsChanged = updates =>
                contacts != null
                    ? contacts.ApplyRemoteContactUpdatesAsync(AccountContactUpdateMap.From(updates))
                    : Task.CompletedTask;
            bridge.SelfPushNameChanged = name =>
                contacts != null
                    ? contacts.ApplyRemoteSelfPushNameAsync(name)
                    : Task.CompletedTask;
            bridge.FavoritesListChanged = ids =>
                chatFacade is ChatFacade favoriteChats
                    ? favoriteChats.ApplyRemoteFavoritesListAsync(ids)
                    : Task.CompletedTask;
            bridge.GroupSubjectChanged = change =>
                ApplyGroupSubjectChangedAsync(contacts, messages, change);
            bridge.GroupParticipantsJoined = joins =>
                messages != null
                    ? messages.NotifyGroupJoinsAsync(joins)
                    : Task.CompletedTask;
            bridge.CallEvents = events =>
                messages != null
                    ? messages.NotifyCallEventsAsync(events)
                    : Task.CompletedTask;
            bridge.GroupParticipantsLeft = leaves =>
                messages != null
                    ? messages.NotifyGroupLeavesAsync(leaves)
                    : Task.CompletedTask;
            bridge.GroupSelfMembershipChanged = (groupJid, participantJids, stillMember) =>
                groups != null
                    ? groups.NoteSelfGroupMembershipAsync(groupJid, participantJids, stillMember)
                    : Task.CompletedTask;
            bridge.GroupRosterEventReceived = change => groups?.ApplyRosterEvent(change);
            bridge.ChatDeleted = jid =>
                chatFacade != null
                    ? chatFacade.ApplyAppStateDeleteChatAsync(jid)
                    : Task.CompletedTask;
            bridge.MessageDeleted = key =>
                messages != null
                    ? messages.ApplyAppStateDeleteMessageAsync(key.RemoteJid, key.Id)
                    : Task.FromResult(false);
            bridge.ResolveSentMessage = resolveSentMessage;
        }

        private static async Task ApplyGroupSubjectChangedAsync(
            IContactService contacts,
            IMessageService messages,
            Unison.Core.Models.GroupSubjectChangeNotifyRequest change)
        {
            if (change == null || string.IsNullOrWhiteSpace(change.GroupJid))
            {
                return;
            }

            string previous = null;
            if (contacts != null && change.EmitTimelineLine)
            {
                previous = contacts.ResolveDisplayName(change.GroupJid, "group-subject-previous");
            }

            if (contacts != null && !string.IsNullOrWhiteSpace(change.NewSubject))
            {
                await contacts.ApplyRemoteGroupSubjectsAsync(
                        new[]
                        {
                            new KeyValuePair<string, string>(change.GroupJid, change.NewSubject)
                        })
                    .ConfigureAwait(false);
            }

            if (change.EmitTimelineLine &&
                messages != null &&
                !string.IsNullOrWhiteSpace(change.NewSubject))
            {
                await messages.NotifyGroupSubjectChangedAsync(change, previous)
                    .ConfigureAwait(false);
            }
        }
    }
}
