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
using Unison.Uwp.Services.WhatsApp.Groups;
using Unison.Uwp.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {

        private bool HasGroupChats()
        {
            return Chats.Any(c => c != null && (c.IsGroup || IsGroupJid(c.JID)));
        }

        private bool IsGroupJid(string jid)
        {
            return !string.IsNullOrWhiteSpace(jid) &&
                   NormalizeJid(jid).EndsWith("@g.us", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsGroupIdPlaceholder(string label, string groupJid) =>
            GroupIdPlaceholder.IsIdPlaceholder(label, groupJid);

        /// <summary>
        /// Whether we already hold a usable name for this group, counting the label on the
        /// row as well as the cached one.
        /// </summary>
        /// <remarks>
        /// The row counts because a name can reach it before it reaches the cache. Written
        /// three ways across the group paths and only one consulted the row, so a synced
        /// subject that the blacklist would otherwise hold back could overwrite a good name
        /// through one path and not the other.
        /// </remarks>
        private bool HasMeaningfulGroupLabel(string groupJid, string rowName)
        {
            string cached;
            if (ContactNames.TryGetValue(groupJid, out cached) &&
                IsMeaningfulChatLabel(cached, groupJid, true))
            {
                return true;
            }

            return IsMeaningfulChatLabel(rowName, groupJid, true);
        }

        private void ApplyGroupSendPermissionsFromMetadata(BinaryNode response, string groupJid)
        {
            _ = _groupApply.ApplyMetadataAsync(groupJid, _groupMetadata.ReadMetadata(response, groupJid));
        }
    }
}
