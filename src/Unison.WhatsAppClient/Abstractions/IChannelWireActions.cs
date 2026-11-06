// =============================================================================
// IChannelWireActions
//
// Channel (@newsletter) WMex calls that today go through the UWP bridge socket.
// ChannelFacade calls this port; the adapter forwards to IWhatsAppSocket.
// =============================================================================
using System.Threading.Tasks;

namespace Unison.WhatsAppClient.Abstractions
{
    /// <summary>The create answer, flattened (portable; no Socket types).</summary>
    public sealed class ChannelCreatedSnapshot
    {
        public string Jid { get; set; }

        /// <summary>Wire <c>viewer_metadata.role</c>; null when the reply omitted it.</summary>
        public string ViewerRole { get; set; }
    }

    public interface IChannelWireActions
    {
        /// <summary>Null socket or handshake incomplete — WMex calls are not safe.</summary>
        bool IsHandshakeComplete { get; }

        /// <summary>Baileys <c>newsletterCreate</c>. Throws when refused.</summary>
        Task<ChannelCreatedSnapshot> CreateChannelAsync(string name, string description);

        /// <summary>Baileys <c>newsletterUpdatePicture</c>. Square JPEG only; throws when refused.</summary>
        Task UpdateChannelPictureAsync(string channelJid, byte[] jpeg);

        /// <summary><c>viewer_metadata.role</c> from newsletterMetadata; null when it could not be read.</summary>
        Task<string> FetchViewerRoleAsync(string channelJid);
    }
}
