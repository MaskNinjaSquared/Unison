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
using Unison.Uwp.Helpers;

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
using Microsoft.Extensions.DependencyInjection;

namespace Unison.Uwp.Services.WhatsApp
{
    public partial class WhatsAppService
    {


        /// <summary>If source is ogg/opus, prefer m4a (MF) then WAV (Concentus) for Mobile playback.</summary>
        private async Task<string> EnsurePlayableAudioUriAsync(ChatMessage message, string sourceUri)
        {
            if (message == null || string.IsNullOrWhiteSpace(sourceUri))
            {
                return sourceUri;
            }

            PlayableAudioFile playable = await _mediaDerivation.EnsurePlayableAsync(
                message.AudioMimeType,
                sourceUri,
                message.Id);
            if (playable == null ||
                string.IsNullOrWhiteSpace(playable.Uri) ||
                string.Equals(playable.Uri, sourceUri, StringComparison.Ordinal))
            {
                return sourceUri;
            }

            message.AudioUri = playable.Uri;
            message.AudioMimeType = playable.MimeType;
            string chatJid = GetCanonicalJid(message.RemoteJid);
            if (!string.IsNullOrWhiteSpace(chatJid))
            {
                try
                {
                    await SaveMessageAsync(chatJid, message);
                    QueueChatMessagesChanged(chatJid);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("[WhatsAppService] Persist playable uri failed: " + ex.Message);
                }
            }

            return playable.Uri;
        }



        Task<byte[]> IMediaDownloadHost.DownloadAndDecryptAsync(
            string url,
            string directPath,
            byte[] mediaKey,
            string mediaType,
            byte[] expectedFileEncSha256)
        {
            return _socket.DownloadAndDecryptMediaAsync(url, directPath, mediaKey, mediaType, expectedFileEncSha256);
        }

        Task IMediaDownloadHost.SaveMessageQuietAsync(string chatJid, ChatMessage message)
        {
            return SaveMessageAsync(chatJid, message);
        }

        void IMediaDownloadHost.NotifyMessagesChanged(string chatJid)
        {
            QueueChatMessagesChanged(chatJid);
        }

        bool IMediaDownloadHost.HasLiveSocket => _socket != null;

    }
}
