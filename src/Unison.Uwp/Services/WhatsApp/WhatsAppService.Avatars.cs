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
        /// <summary>Delegates to <see cref="IContactService"/> (owns dedup/backoff policy); this class only supplies the fetch primitive.</summary>
        private void RequestAvatarRefresh(ChatItem chat, bool force = false)
        {
            _contactService?.RequestAvatarRefresh(chat, force);
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
