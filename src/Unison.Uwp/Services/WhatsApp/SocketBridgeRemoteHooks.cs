// =============================================================================
// SocketBridgeRemoteHooks
//
// Wires SocketBridge app-state / contact callbacks onto the façades. Kept out of
// WhatsAppService.BuildSocketBridge so connect open only constructs + assigns.
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
            bridge.GroupSubjectChanged = (jid, subject) =>
                contacts != null
                    ? contacts.ApplyRemoteGroupSubjectsAsync(
                        new[] { new KeyValuePair<string, string>(jid, subject) })
                    : Task.CompletedTask;
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
    }
}
