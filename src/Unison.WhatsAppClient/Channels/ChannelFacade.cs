// =============================================================================
// ChannelFacade
//
// Channels this account creates or administers. The role is the only thing the
// UI needs from here today: WhatsApp lets the owner and admins post, followers
// only read. It is stored on the chat row so an owner gets the composer offline
// and after a restart; the server's answer on each open wins and is written
// back only when it differs, so a demotion on the phone shows up next open.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Core.Contracts;
using Unison.Core.Contracts.WhatsApp;
using Unison.Core.Helpers;
using Unison.Core.Helpers.Channels;
using Unison.Core.Models;
using Unison.WhatsAppClient.Abstractions;

namespace Unison.WhatsAppClient.Channels
{
    public sealed class ChannelFacade : IChannelService
    {
        private readonly IChannelWireActions _wire;
        private readonly IDispatcher _ui;
        private readonly IChatStore _store;
        private readonly IProfilePictureEncoder _pictures;

        public ChannelFacade(
            IChannelWireActions wire,
            IDispatcher ui,
            IChatStore store,
            IProfilePictureEncoder pictures = null)
        {
            _wire = wire ?? throw new ArgumentNullException(nameof(wire));
            _ui = ui ?? throw new ArgumentNullException(nameof(ui));
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _pictures = pictures;
        }

        public async Task<ChannelCreateResult> CreateChannelAsync(ChannelCreateRequest request)
        {
            string name = request?.Name?.Trim();
            if (string.IsNullOrEmpty(name))
            {
                return ChannelCreateResult.Failed(ChannelCreateResult.ReasonEmptyName);
            }

            if (!_wire.IsHandshakeComplete)
            {
                return ChannelCreateResult.Failed(ChannelCreateResult.ReasonNotConnected);
            }

            ChannelCreatedSnapshot created;
            try
            {
                created = await _wire
                    .CreateChannelAsync(name, request.Description?.Trim() ?? string.Empty)
                    .ConfigureAwait(false);
                Debug.WriteLine("[ChannelFacade] created channel " + (created?.Jid ?? "(none)"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChannelFacade] CreateChannelAsync: " + ex.Message);
                return ChannelCreateResult.Failed(ex.Message);
            }

            string jid = JidHelper.Normalize(created?.Jid);
            if (string.IsNullOrWhiteSpace(jid))
            {
                return ChannelCreateResult.Failed(ChannelCreateResult.ReasonNoChannel);
            }

            // The creator is the owner by definition; some create replies omit viewer_metadata.
            ChannelViewerRole role = ChannelViewerRoleParse.FromWire(created.ViewerRole);
            await StoreRoleAsync(jid, role == ChannelViewerRole.Unknown ? ChannelViewerRole.Owner : role)
                .ConfigureAwait(false);

            return new ChannelCreateResult
            {
                ChannelJid = jid,
                PhotoFailed = !await TrySetPictureAsync(jid, request.PhotoBytes).ConfigureAwait(false)
            };
        }

        public async Task RefreshViewerRoleAsync(ChatItem channel)
        {
            if (channel == null || !channel.IsChannel || string.IsNullOrWhiteSpace(channel.JID))
            {
                return;
            }

            string jid = JidHelper.Normalize(channel.JID);
            ChannelViewerRole known = StoredRole(jid);
            if (known != ChannelViewerRole.Unknown && channel.ChannelRole != known)
            {
                await _ui.RunAsync(() => channel.ChannelRole = known).ConfigureAwait(false);
            }

            if (!_wire.IsHandshakeComplete)
            {
                return;
            }

            string wireRole;
            try
            {
                wireRole = await _wire.FetchViewerRoleAsync(jid).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChannelFacade] viewer role for " + jid + ": " + ex.Message);
                return;
            }

            ChannelViewerRole fetched = ChannelViewerRoleParse.FromWire(wireRole);
            if (fetched == ChannelViewerRole.Unknown)
            {
                return;
            }

            if (fetched != known)
            {
                await StoreRoleAsync(jid, fetched).ConfigureAwait(false);
            }

            await _ui.RunAsync(() => channel.ChannelRole = fetched).ConfigureAwait(false);
        }

        /// <returns>True when there was nothing to set or the picture went up.</returns>
        private async Task<bool> TrySetPictureAsync(string jid, byte[] photo)
        {
            if (photo == null || photo.Length == 0)
            {
                return true;
            }

            if (_pictures == null)
            {
                Debug.WriteLine("[ChannelFacade] picture skipped: no encoder");
                return false;
            }

            try
            {
                byte[] jpeg = await _pictures.EncodeSquareJpegAsync(photo).ConfigureAwait(false);
                if (jpeg == null || jpeg.Length == 0)
                {
                    Debug.WriteLine("[ChannelFacade] picture skipped: could not decode");
                    return false;
                }

                await _wire.UpdateChannelPictureAsync(jid, jpeg).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChannelFacade] picture: " + ex.Message);
                return false;
            }
        }

        private ChannelViewerRole StoredRole(string jid)
        {
            ChatLocalState state = _store.TryGetCached(jid);
            return state != null && state.Knows(ChatLocalStateFields.ChannelRole)
                ? state.ChannelRole
                : ChannelViewerRole.Unknown;
        }

        private async Task StoreRoleAsync(string jid, ChannelViewerRole role)
        {
            try
            {
                await _store.SetChannelRoleAsync(jid, role).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[ChannelFacade] store role for " + jid + ": " + ex.Message);
            }
        }
    }
}
