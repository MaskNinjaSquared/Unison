// =============================================================================
// OfferCallUseCase
//
// Places a 1:1 audio offer (meowcaller placeCall): discover peer devices, mint
// callKey, Signal-encrypt Message{Call{CallKey}} per device, BuildOffer, send.
// =============================================================================
using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Messages;
using Unison.Socket.Signal;
using Unison.Socket.Session;
using Unison.Socket.UseCases.Messages;

namespace Unison.Socket.UseCases.Calls
{
    public sealed class OfferCallUseCase
    {
        private readonly ConnectionHandler _connection;
        private readonly ISignalRepository _signal;
        private readonly GetUSyncDevicesUseCase _devices;
        private readonly AuthState _auth;
        private readonly Func<string> _meId;
        private readonly Func<string> _meLid;
        private readonly ISocketLog _log;

        public OfferCallUseCase(
            ConnectionHandler connection,
            ISignalRepository signal,
            GetUSyncDevicesUseCase devices,
            AuthState auth,
            Func<string> meId,
            Func<string> meLid,
            ISocketLog log = null)
        {
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _signal = signal ?? throw new ArgumentNullException(nameof(signal));
            _devices = devices ?? throw new ArgumentNullException(nameof(devices));
            _auth = auth ?? throw new ArgumentNullException(nameof(auth));
            _meId = meId ?? (() => null);
            _meLid = meLid ?? (() => null);
            _log = log ?? NullSocketLog.Instance;
        }

        /// <summary>
        /// Sends an audio offer to <paramref name="peerJid"/> (PN or LID).
        /// Returns call id + callKey for the façade ActiveCall / media path.
        /// </summary>
        public async Task<OfferCallResult> ExecuteAsync(string peerJid)
        {
            if (string.IsNullOrWhiteSpace(peerJid))
            {
                throw new ArgumentException("peerJid is required", nameof(peerJid));
            }

            string meLid = _meLid();
            string meId = _meId();
            string creator = !string.IsNullOrEmpty(meLid) ? meLid : meId;
            if (string.IsNullOrEmpty(creator))
            {
                throw new InvalidOperationException("No self identity for call offer.");
            }

            IReadOnlyList<DeviceJid> devices = await _devices
                .ExecuteAsync(new[] { peerJid.Trim() }, useCache: true, ignoreZeroDevices: false)
                .ConfigureAwait(false);
            if (devices == null || devices.Count == 0)
            {
                throw new InvalidOperationException(
                    "Peer has no devices (unreachable / not on WhatsApp): " + peerJid);
            }

            string toJid = PreferAccountJid(devices[0], peerJid.Trim());

            byte[] callKey = new byte[32];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(callKey);
            }

            var envelope = new global::Proto.Message
            {
                Call = new global::Proto.Message.Types.Call
                {
                    CallKey = ByteString.CopyFrom(callKey)
                }
            };
            byte[] plain = envelope.ToByteArray();

            var keys = new List<CallStanzaBuilders.OfferDeviceKey>(devices.Count);
            bool needIdentity = false;
            for (int i = 0; i < devices.Count; i++)
            {
                DeviceJid device = devices[i];
                if (device == null || string.IsNullOrEmpty(device.Jid))
                {
                    continue;
                }

                EncryptedPayload enc = await _signal
                    .EncryptMessageAsync(device.Jid, plain)
                    .ConfigureAwait(false);
                if (enc == null || enc.Ciphertext == null || enc.Ciphertext.Length == 0)
                {
                    _log.Warn("[Call] offer encrypt failed for " + device.Jid);
                    continue;
                }

                if (enc.IsPreKeyMessage)
                {
                    needIdentity = true;
                }

                keys.Add(new CallStanzaBuilders.OfferDeviceKey
                {
                    DeviceJid = device.Jid,
                    Ciphertext = enc.Ciphertext,
                    EncType = enc.Type
                });
            }

            if (keys.Count == 0)
            {
                throw new InvalidOperationException("Could not encrypt callKey for any peer device.");
            }

            byte[] deviceIdentity = null;
            if (needIdentity && _auth.Account != null)
            {
                deviceIdentity = KeyBundleNodes.EncodeSignedDeviceIdentity(_auth.Account, true);
            }

            string callId = NewCallId();
            string stanzaId = !string.IsNullOrEmpty(meId)
                ? MessageContent.GenerateMessageId(meId)
                : _connection.GenerateMessageTag();

            BinaryNode node = CallStanzaBuilders.BuildOffer(
                callId,
                toJid,
                creator,
                stanzaId,
                keys,
                privacyToken: null,
                capability: CallStanzaBuilders.CapabilityOfferBlob,
                deviceIdentity: deviceIdentity,
                isVideo: false);

            _log.Info(
                "[Call] offer send callId=" + callId +
                " to=" + toJid +
                " creator=" + creator +
                " devices=" + keys.Count +
                " id=" + stanzaId);

            await _connection.SendNodeAsync(node).ConfigureAwait(false);
            _log.Info("[Call] offer sent callId=" + callId);

            return new OfferCallResult
            {
                CallId = callId,
                PeerJid = toJid,
                CallCreatorJid = creator,
                CallKey = callKey
            };
        }

        private static string PreferAccountJid(DeviceJid firstDevice, string requested)
        {
            if (firstDevice == null || string.IsNullOrEmpty(firstDevice.User))
            {
                return requested;
            }

            string server = string.IsNullOrEmpty(firstDevice.Server) ? "s.whatsapp.net" : firstDevice.Server;
            // Offer to= is the account (no device), matching meowcaller peer LID/PN.
            return firstDevice.User + "@" + server;
        }

        private static string NewCallId()
        {
            var bytes = new byte[16];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    public sealed class OfferCallResult
    {
        public string CallId { get; set; }

        public string PeerJid { get; set; }

        public string CallCreatorJid { get; set; }

        public byte[] CallKey { get; set; }
    }
}
