// =============================================================================
// BridgeChannelWireActions
//
// Forwards IChannelWireActions to the live IWhatsAppSocket from the bridge.
// =============================================================================
using System;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Abstractions;
using UwpSessionProvider = Unison.Uwp.Services.Socket.IWhatsAppSessionProvider;

namespace Unison.Uwp.Services.WhatsApp.Channels
{
    /// <summary>
    /// Internal: constructor takes the UWP bridge provider (<c>internal</c>), so this type
    /// cannot be public without widening that interface.
    /// </summary>
    internal sealed class BridgeChannelWireActions : IChannelWireActions
    {
        private readonly UwpSessionProvider _sessions;

        public BridgeChannelWireActions(UwpSessionProvider sessions)
        {
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
        }

        public bool IsHandshakeComplete
        {
            get
            {
                IWhatsAppSocket socket = _sessions.Socket;
                return socket != null && socket.IsHandshakeComplete;
            }
        }

        public async Task<ChannelCreatedSnapshot> CreateChannelAsync(string name, string description)
        {
            IWhatsAppSocket socket = RequireSocket("channel create");
            Unison.Socket.UseCases.Newsletter.NewsletterMetadataResult created =
                await socket.CreateNewsletterAsync(name, description).ConfigureAwait(false);
            return new ChannelCreatedSnapshot
            {
                Jid = created?.Id,
                ViewerRole = created?.ViewerRole
            };
        }

        public Task UpdateChannelPictureAsync(string channelJid, byte[] jpeg)
        {
            IWhatsAppSocket socket = RequireSocket("channel picture");
            return socket.UpdateNewsletterPictureAsync(channelJid, jpeg);
        }

        public async Task<string> FetchViewerRoleAsync(string channelJid)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                return null;
            }

            Unison.Socket.UseCases.Newsletter.NewsletterMetadataResult meta =
                await socket.GetNewsletterMetadataAsync(channelJid).ConfigureAwait(false);
            return meta?.ViewerRole;
        }

        private IWhatsAppSocket RequireSocket(string operation)
        {
            IWhatsAppSocket socket = _sessions.Socket;
            if (socket == null || !socket.IsHandshakeComplete)
            {
                throw new InvalidOperationException("Not connected: " + operation + " was not sent.");
            }

            return socket;
        }
    }
}
