using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.Core.Helpers;
using Unison.Core.Mappers;
using Unison.Core.Models;
using Unison.WhatsAppClient.Helpers;
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using System.Threading;
using System.Runtime.InteropServices.WindowsRuntime;

using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unison.Background;
using Unison.Baileys.Diagnostics;
using Unison.Baileys.Client;
using Unison.Core.Constants;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.State;
using Unison.Socket.UseCases.Contacts;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        internal async Task ApplyAppStateSelfPushNameAsync(string name)
        {
            if (_authState?.Me == null)
            {
                return;
            }

            string selfJid = NormalizeJid(_authState.Me.Id);
            string sanitized = SanitizeContactLabel(name, selfJid);
            if (string.IsNullOrWhiteSpace(sanitized))
            {
                return;
            }

            if (Contacts != null)
            {
                Contacts.CaptureSelfPushName(sanitized, SelfPushNameCapture.AppStateSource);
            }

            await PersistAuthStateAsync(null, "apply-self-contact-name");
            if (Contacts != null)
            {
                await Contacts.ApplyRemoteContactNameAsync(selfJid, sanitized).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Durable half of a chat deletion. RAM + badge are owned by
        /// <see cref="IChatService.ApplyAppStateDeleteChatAsync"/>.
        /// </summary>
        Task IAppStateLocalHost.ForgetChatStorageAsync(
            string canonicalJid,
            IReadOnlyList<string> storeKeys) =>
            ForgetChatInStorageAsync(canonicalJid, storeKeys);

        /// <summary>App-state / SocketBridge entry: forwards to <see cref="IChatService"/>.</summary>
        internal Task ApplyAppStateDeleteChatAsync(string jid) =>
            ChatFacade != null
                ? ChatFacade.ApplyAppStateDeleteChatAsync(jid)
                : Task.CompletedTask;

        /// <summary>
        /// The persistent half of a chat deletion: tombstone the list preview, drop the stored
        /// messages, and take the chat out of the catalog the app rehydrates from.
        /// </summary>
        private async Task ForgetChatInStorageAsync(string canonical, IReadOnlyList<string> keys)
        {
            var jids = new List<string>();
            if (keys != null)
            {
                jids.AddRange(keys);
            }

            if (!jids.Any(k => string.Equals(k, canonical, StringComparison.OrdinalIgnoreCase)))
            {
                jids.Add(canonical);
            }

            if (_chatPreviews != null)
            {
                try
                {
                    await _chatPreviews.MarkDeletedAsync(jids, DateTime.UtcNow).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat preview tombstone failed: " + ex.Message);
                }
            }

            if (_historyMessages != null)
            {
                try
                {
                    await _historyMessages.DeleteForChatKeysAsync(jids).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Chat message delete failed: " + ex.Message);
                }
            }

            try
            {
                _messageStore?.ClearMemoryCache();
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Message cache clear failed: " + ex.Message);
            }

            try
            {
                // Snapshot on the UI thread. By here we are several awaits deep on a pool
                // thread, and Chats is bound to the list: enumerating it while the UI adds a
                // row throws, and the catch below would turn that into a silently skipped save.
                List<ChatItem> snapshot = null;
                await _ui.RunAsync(() => snapshot = Chats.Where(c => c != null).ToList());
                await PersistChatCatalogAsync(snapshot ?? new List<ChatItem>()).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[WhatsAppService] Chat catalog persist failed: " + ex.Message);
            }
        }

        /// <summary>App-state / SocketBridge entry: forwards to <see cref="IMessageService"/>.</summary>
        internal Task<bool> ApplyAppStateDeleteMessageAsync(string jid, string messageId) =>
            Messages != null
                ? Messages.ApplyAppStateDeleteMessageAsync(jid, messageId)
                : Task.FromResult(false);

    }
}
