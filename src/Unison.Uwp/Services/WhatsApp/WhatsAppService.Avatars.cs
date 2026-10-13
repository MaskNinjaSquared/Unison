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
using Unison.Baileys.Protocol;
using Unison.Uwp.Data;
using Unison.Baileys.Crypto;
using Unison.Uwp.Transport;
using Proto;
using Google.Protobuf;
using Windows.UI.Core;
using System.Threading;
using Windows.Storage;
using Windows.ApplicationModel.Core;
using Windows.Networking.Sockets;
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

        public Task<string> GetProfilePictureUrlAsync(string jid, string type = "preview")
        {
            return _avatarFetcher.FetchUrlAsync(jid, type);
        }

        public void MarkAvatarImageLoadFailed(ChatItem chat, string reason)
        {
            if (chat == null)
            {
                return;
            }

            _avatarCache.DeleteIfCached(chat.AvatarUrl);

            // Nao mantenha uma URI local quebrada nem aplique o backoff de 30 minutos:
            // isso fazia a foto desaparecer durante toda a sessao. A linha visivel pede
            // uma nova consulta imediatamente, tentando tambem o JID alternativo PN/LID.
            chat.AvatarUrl = null;
            chat.AvatarFetchedAtUtc = null;
            chat.AvatarFetchFailedAtUtc = null;
            chat.AvatarFetchFailureReason = string.IsNullOrWhiteSpace(reason) ? "ui-image-failed" : reason;
            Debug.WriteLine($"[WhatsAppService] UI avatar image load failed for {chat.JID}: {chat.AvatarFetchFailureReason}");
            RequestAvatarRefresh(chat, force: true);
            SchedulePersist();
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns dedup/backoff policy); this class only supplies the fetch primitive.</summary>
        public void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _contactService?.RequestAvatarRefresh(chat, force);
        }

        /// <summary>Delegates to <see cref="IContactService"/> (owns batch/backoff policy); this class only supplies the fetch primitives.</summary>
        public Task RetrieveContactPicturesCoreAsync(CancellationToken token)
        {
            if (_socket == null)
            {
                return Task.CompletedTask;
            }

            return _contactService?.RetrieveContactPicturesAsync(token) ?? Task.CompletedTask;
        }

        private string FindAvatarOnChatRows(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return null;
            }

            foreach (var row in GetChatRowsForCanonicalJid(jid))
            {
                if (row != null && !string.IsNullOrWhiteSpace(row.AvatarUrl))
                {
                    return row.AvatarUrl;
                }
            }

            return null;
        }

        public Task<string> GetProfilePictureAsync(string jid)
        {
            return _avatarFetcher.FetchAndCacheAsync(jid, CancellationToken.None);
        }
    }
}
