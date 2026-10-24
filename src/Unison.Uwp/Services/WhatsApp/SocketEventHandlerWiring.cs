// =============================================================================
// SocketEventHandlerWiring
//
// Event wiring body. Called from UwpConnectionSocketSessionFactory so portable
// ConnectionConnectOpen never names IWhatsAppSocket; UWP-only.
// =============================================================================
using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.Uwp.Client;
using Unison.WhatsAppClient.Helpers;

namespace Unison.Uwp.Services.WhatsApp
{
    internal static class SocketEventHandlerWiring
    {
        public static void Attach(IWhatsAppSocket socket, WhatsAppService owner)
        {
            if (socket == null)
            {
                throw new ArgumentNullException(nameof(socket));
            }

            if (owner == null)
            {
                throw new ArgumentNullException(nameof(owner));
            }

            socket.OnQRCodeReceived += (s, qr) =>
                QrCodeWireDispatch.Handle(owner, qr, owner.IsCurrentSocket(s));

            socket.OnPresenceUpdate += (s, e) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                SocketFacadeWireDispatch.HandlePresence(owner, e);
            };
            owner.RegisterSocketAliases("service-known-aliases");

            socket.OnAuthStateUpdate += async (s, e) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                await AuthStateWireDispatch.RunAsync(owner).ConfigureAwait(false);
            };

            socket.OnConnectionUpdate += (s, status) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    Debug.WriteLine("[WhatsAppService] Ignoring stale socket status: " + status);
                    return;
                }

                ConnectionUpdateDispatch.Handle(owner, status);
            };

            socket.OnSessionInitialized += async (s, e) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    owner.PairingTrace("OnSessionInitialized IGNORED (stale socket)");
                    return;
                }

                await SessionInitializedDispatch.RunAsync(owner).ConfigureAwait(false);
            };

            socket.OnStreamError += (s, code) =>
                SocketErrorDispatch.HandleStreamError(owner, code, owner.IsCurrentSocket(s));

            socket.OnError += async (s, ex) =>
                await SocketErrorDispatch.HandleErrorAsync(
                    owner,
                    ex,
                    owner.IsCurrentSocket(s),
                    socket.IsConnected,
                    socket.IsHandshakeComplete).ConfigureAwait(false);

            socket.OnMessage += (s, node) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                InboundWireNodeDispatch.Handle(owner, node);
            };

            // Not gated on current socket — see HistorySyncWireApply header.
            socket.OnHistorySyncReceived += (s, sync) =>
                HistorySyncWireApply.Run(owner, sync);

            socket.OnDecryptedMessageReceived += (s, e) =>
            {
                if (!owner.IsCurrentSocket(s) || e == null)
                {
                    return Task.CompletedTask;
                }

                DecryptedMessageWireDispatch.Handle(
                    owner,
                    e.FromJid,
                    e.Participant,
                    e.ParticipantAlt,
                    e.AddressingMode,
                    e.MessageId,
                    e.Message,
                    e.Timestamp,
                    e.IsFromMe,
                    e.PushName,
                    e.VerifiedName,
                    e.SenderLid,
                    e.PeerRecipientPn,
                    e.PeerRecipientLid,
                    e.RecipientJid,
                    e.IsOffline);
                return Task.CompletedTask;
            };

            socket.OnMissingMessageDetected += (s, e) =>
            {
                if (!owner.IsCurrentSocket(s) || e == null)
                {
                    return;
                }

                SocketFacadeWireDispatch.HandleMissingMessage(
                    owner,
                    e.ChatJid,
                    e.Participant,
                    e.MessageId,
                    e.IsFromMe,
                    e.Timestamp,
                    e.Reason);
            };

            socket.OnOutgoingMessageStatusChanged += (s, e) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                _ = SocketFacadeWireDispatch.HandleOutgoingStatusAsync(
                    owner,
                    e?.MessageId,
                    e?.Status,
                    e?.Error);
            };

            socket.OnReceiptReceived += (s, node) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                _ = SocketFacadeWireDispatch.HandleReceiptAsync(owner, node);
            };

            socket.OnLinkCodeCompanionReg += (s, node) =>
                SocketFacadeWireDispatch.HandleLinkCodeCompanion(
                    owner,
                    node,
                    owner.IsCurrentSocket(s));

            socket.OnReceivedPendingNotifications += async (s, offlineCount) =>
            {
                if (!owner.IsCurrentSocket(s))
                {
                    return;
                }

                await OfflineReplayRelease.RunAsync(owner, offlineCount).ConfigureAwait(false);
            };

            // Dirty bits and server_sync are answered inside the session now: the socket layer
            // clears the flag and its own app state module runs the resync, so there is nothing
            // for the service to coordinate.
        }
    }
}
