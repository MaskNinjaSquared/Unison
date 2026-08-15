// =============================================================================
// PairingFlow
//
// Drives companion login: QR (pair-device / pair-success) and phone-number
// link-code (companion_hello / companion_finish).
//
// This is the first feature module and the template for the rest. It plugs into
// ConnectionHandler.Dispatcher and the handler has no idea it exists, which is
// how pairing, messaging and groups can evolve without ever touching the class
// that owns the wire.
//
// Ports: rc14 the 'CB:iq,type:set,pair-device' and 'CB:iq,,pair-success'
//        handlers in src/Socket/socket.ts, requestPairingCode / generatePairingKey
//        in the same file, and the 'link_code_companion_reg' notification in
//        src/Socket/messages-recv.ts
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Unison.Baileys.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;
using Unison.Socket.Abstractions;
using Unison.Socket.Events;

namespace Unison.Socket.Session.Pairing
{
    /// <summary>
    /// Drives companion pairing: rotating QR, or the eight-character phone code from rc14.
    /// It plugs into <see cref="ConnectionHandler.Dispatcher"/> and the handler knows nothing
    /// about it.
    /// </summary>
    public sealed class PairingFlow : IDisposable
    {
        // rc14 src/Utils/generics.ts CROCKFORD_CHARACTERS — not the textbook Crockford set.
        private const string CrockfordAlphabet = "123456789ABCDEFGHJKLMNPQRSTVWXYZ";

        private readonly ConnectionHandler _handler;
        private readonly AuthState _auth;
        private readonly IWaEventBus _events;
        private readonly SocketConfig _config;
        private readonly ISocketLog _log;

        private readonly List<IDisposable> _registrations = new List<IDisposable>();
        private readonly object _qrGate = new object();

        private Queue<string> _pendingRefs;
        private Timer _qrTimer;
        private bool _disposed;

        public PairingFlow(
            ConnectionHandler handler,
            AuthState auth,
            IWaEventBus events,
            SocketConfig config = null,
            ISocketLog log = null)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            if (events == null)
            {
                throw new ArgumentNullException(nameof(events));
            }

            _handler = handler;
            _auth = auth;
            _events = events;
            _config = config ?? new SocketConfig();
            _log = log ?? NullSocketLog.Instance;
        }

        public void Attach()
        {
            _registrations.Add(_handler.RegisterNodeHandler("iq,type:set,pair-device", OnPairDeviceAsync));
            _registrations.Add(_handler.RegisterNodeHandler("iq,,pair-success", OnPairSuccessAsync));
            _registrations.Add(_handler.RegisterNodeHandler(
                "notification,type:link_code_companion_reg",
                OnLinkCodeCompanionRegAsync));
            _handler.RegisterSocketEndHandler(_ => { StopQrTimer(); return Task.FromResult(true); });
        }

        /// <summary>
        /// Asks WhatsApp for the eight-character code the user types on their phone.
        /// The socket must already be connected and unregistered, as in rc14.
        /// </summary>
        public async Task<string> RequestPairingCodeAsync(string phoneNumber, string customPairingCode = null)
        {
            if (string.IsNullOrWhiteSpace(phoneNumber))
            {
                throw new ArgumentException("Phone number is required", nameof(phoneNumber));
            }

            if (!_handler.IsConnected)
            {
                throw new InvalidOperationException("Cannot request a pairing code before the socket is connected.");
            }

            if (!string.IsNullOrEmpty(customPairingCode) && customPairingCode.Length != 8)
            {
                throw new ArgumentException("Custom pairing code must be exactly 8 chars", nameof(customPairingCode));
            }

            StopQrTimer();

            if (_auth.PairingEphemeralKeyPair == null)
            {
                _auth.PairingEphemeralKeyPair = CryptoUtils.GenerateKeyPair();
            }

            string pairingCode = !string.IsNullOrEmpty(customPairingCode)
                ? customPairingCode
                : BytesToCrockford(CryptoUtils.RandomBytes(5));

            _auth.PairingCode = pairingCode;
            _auth.Me = new UserInfo
            {
                Id = WA.JidEncode(phoneNumber, WA.S_WHATSAPP_NET),
                Name = "~",
                Phone = phoneNumber
            };

            await _events.EmitAsync(WaEventKind.CredsUpdate, _auth).ConfigureAwait(false);

            var browser = _config.Browser ?? new[] { "Mac OS", "Chrome", "14.4.1" };
            var platformId = CompanionRegistration.GetCompanionPlatformId(browser);
            var platformDisplay = (browser.Length > 1 ? browser[1] : "Chrome") +
                                  " (" + (browser.Length > 0 ? browser[0] : "Mac OS") + ")";

            await _handler.SendNodeAsync(new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "id", _handler.GenerateMessageTag() },
                    { "xmlns", "md" }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "link_code_companion_reg",
                        new Dictionary<string, string>
                        {
                            { "jid", _auth.Me.Id },
                            { "stage", "companion_hello" },
                            { "should_show_push_notification", "true" }
                        },
                        new List<BinaryNode>
                        {
                            Leaf("link_code_pairing_wrapped_companion_ephemeral_pub", GeneratePairingKey()),
                            Leaf("companion_server_auth_key_pub", _auth.NoiseKey.Public),
                            Leaf("companion_platform_id", platformId),
                            Leaf("companion_platform_display", platformDisplay),
                            Leaf("link_code_pairing_nonce", "0")
                        })
                })).ConfigureAwait(false);

            _log.Info("[Pairing] Sent companion_hello for " + _auth.Me.Id);
            return pairingCode;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopQrTimer();

            foreach (var registration in _registrations)
            {
                registration.Dispose();
            }

            _registrations.Clear();
        }

        private async Task OnPairDeviceAsync(BinaryNode stanza)
        {
            await _handler.SendNodeAsync(new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "result" },
                    { "id", stanza.GetAttribute("id") }
                })).ConfigureAwait(false);

            var pairDeviceNode = stanza.GetChild("pair-device");
            var refNodes = pairDeviceNode != null ? pairDeviceNode.GetChildren("ref") : new List<BinaryNode>();

            lock (_qrGate)
            {
                _pendingRefs = new Queue<string>();
                foreach (var refNode in refNodes)
                {
                    var value = refNode.GetContentString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        _pendingRefs.Enqueue(value);
                    }
                }
            }

            _log.Info($"Received {refNodes.Count} QR ref(s)");
            await EmitNextQrAsync().ConfigureAwait(false);
        }

        private async Task EmitNextQrAsync()
        {
            if (!_handler.IsConnected)
            {
                return;
            }

            string reference = null;
            lock (_qrGate)
            {
                if (_pendingRefs != null && _pendingRefs.Count > 0)
                {
                    reference = _pendingRefs.Dequeue();
                }
            }

            if (reference == null)
            {
                _log.Warn("QR refs exhausted");
                await _handler.EndAsync(
                    new WaConnectionException("QR refs attempts ended", DisconnectReason.ConnectionLost))
                    .ConfigureAwait(false);
                return;
            }

            var qr = CompanionRegistration.BuildPairingQrData(
                reference,
                Convert.ToBase64String(_auth.NoiseKey.Public),
                Convert.ToBase64String(_auth.SignedIdentityKey.Public),
                _auth.AdvSecretKey,
                _config.Browser);

            await _events.EmitAsync(WaEventKind.ConnectionUpdate, new ConnectionUpdate { Qr = qr })
                .ConfigureAwait(false);

            ScheduleNextQr();
        }

        private void ScheduleNextQr()
        {
            StopQrTimer();

            lock (_qrGate)
            {
                _qrTimer = new Timer(
                    _ => EmitNextQrAsync().ContinueWith(
                        t => _log.Error("Failed to rotate QR", t.Exception),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default),
                    null,
                    _config.QrTimeout,
                    Timeout.InfiniteTimeSpan);
            }
        }

        private void StopQrTimer()
        {
            lock (_qrGate)
            {
                if (_qrTimer != null)
                {
                    _qrTimer.Dispose();
                    _qrTimer = null;
                }
            }
        }

        private async Task OnPairSuccessAsync(BinaryNode stanza)
        {
            StopQrTimer();

            try
            {
                var result = PairingConfigurator.Configure(stanza, _auth);

                _auth.Me = result.Me;
                _auth.Account = result.Account;
                _auth.Registered = true;

                _log.Info($"Pairing configured for {result.Me.Id} on {result.Platform}, expect a reconnect");

                await _events.EmitAsync(WaEventKind.CredsUpdate, _auth).ConfigureAwait(false);
                await _events.EmitAsync(
                    WaEventKind.ConnectionUpdate,
                    new ConnectionUpdate { IsNewLogin = true, Qr = null }).ConfigureAwait(false);

                await _handler.SendNodeAsync(result.Reply).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("Error in pairing", ex);
                await _handler.EndAsync(ex).ConfigureAwait(false);
            }
        }

        private async Task OnLinkCodeCompanionRegAsync(BinaryNode node)
        {
            if (string.IsNullOrEmpty(_auth.PairingCode))
            {
                _log.Warn("[Pairing] link_code_companion_reg arrived with no pairing code stored");
                return;
            }

            var link = node.GetChild("link_code_companion_reg");
            if (link == null)
            {
                _log.Warn("[Pairing] link_code_companion_reg notification had no payload");
                return;
            }

            try
            {
                var reference = RequireBytes(link, "link_code_pairing_ref");
                var primaryIdentityPublicKey = RequireBytes(link, "primary_identity_pub");
                var wrappedPrimaryEphemeral = RequireBytes(link, "link_code_pairing_wrapped_primary_ephemeral_pub");
                var codePairingPublicKey = DecipherLinkPublicKey(wrappedPrimaryEphemeral);

                var companionSharedKey = CryptoUtils.SharedKey(
                    _auth.PairingEphemeralKeyPair.Private,
                    codePairingPublicKey);

                var random = CryptoUtils.RandomBytes(32);
                var linkCodeSalt = CryptoUtils.RandomBytes(32);
                var linkCodePairingExpanded = CryptoUtils.Hkdf(
                    companionSharedKey,
                    32,
                    linkCodeSalt,
                    "link_code_pairing_key_bundle_encryption_key");

                var encryptPayload = Concat(
                    _auth.SignedIdentityKey.Public,
                    primaryIdentityPublicKey,
                    random);
                var encryptIv = CryptoUtils.RandomBytes(12);
                var encrypted = CryptoUtils.AesGcmEncrypt(
                    encryptPayload,
                    linkCodePairingExpanded,
                    encryptIv,
                    new byte[0]);
                var encryptedPayload = Concat(linkCodeSalt, encryptIv, encrypted);

                var identitySharedKey = CryptoUtils.SharedKey(
                    _auth.SignedIdentityKey.Private,
                    primaryIdentityPublicKey);
                var identityPayload = Concat(companionSharedKey, identitySharedKey, random);
                _auth.AdvSecretKey = Convert.ToBase64String(
                    CryptoUtils.Hkdf(identityPayload, 32, null, "adv_secret"));

                await _handler.QueryAsync(new BinaryNode(
                    "iq",
                    new Dictionary<string, string>
                    {
                        { "to", WA.S_WHATSAPP_NET },
                        { "type", "set" },
                        { "id", _handler.GenerateMessageTag() },
                        { "xmlns", "md" }
                    },
                    new List<BinaryNode>
                    {
                        new BinaryNode(
                            "link_code_companion_reg",
                            FinishAttrs(),
                            new List<BinaryNode>
                            {
                                Leaf("link_code_pairing_wrapped_key_bundle", encryptedPayload),
                                Leaf("companion_identity_public", _auth.SignedIdentityKey.Public),
                                Leaf("link_code_pairing_ref", reference)
                            })
                    })).ConfigureAwait(false);

                StopQrTimer();
                _auth.Registered = true;
                _log.Info("[Pairing] companion_finish accepted, expect a reconnect");

                await _events.EmitAsync(WaEventKind.CredsUpdate, _auth).ConfigureAwait(false);
                await _events.EmitAsync(
                    WaEventKind.ConnectionUpdate,
                    new ConnectionUpdate { IsNewLogin = true, Qr = null }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error("[Pairing] link_code_companion_reg failed", ex);
                await _handler.EndAsync(ex).ConfigureAwait(false);
            }
        }

        private byte[] GeneratePairingKey()
        {
            var salt = CryptoUtils.RandomBytes(32);
            var iv = CryptoUtils.RandomBytes(16);
            var key = CryptoUtils.DerivePairingCodeKey(_auth.PairingCode, salt);
            var ciphered = CryptoUtils.AesCtrEncrypt(_auth.PairingEphemeralKeyPair.Public, key, iv);
            return Concat(salt, iv, ciphered);
        }

        private byte[] DecipherLinkPublicKey(byte[] wrapped)
        {
            if (wrapped == null || wrapped.Length < 80)
            {
                throw new InvalidOperationException("Invalid link-code public key");
            }

            var salt = Slice(wrapped, 0, 32);
            var iv = Slice(wrapped, 32, 16);
            var payload = Slice(wrapped, 48, 32);
            var key = CryptoUtils.DerivePairingCodeKey(_auth.PairingCode, salt);
            return CryptoUtils.AesCtrDecrypt(payload, key, iv);
        }

        private Dictionary<string, string> FinishAttrs()
        {
            var attrs = new Dictionary<string, string>
            {
                { "stage", "companion_finish" }
            };

            if (_auth.Me != null && !string.IsNullOrEmpty(_auth.Me.Id))
            {
                attrs["jid"] = _auth.Me.Id;
            }

            return attrs;
        }

        private static BinaryNode Leaf(string tag, byte[] content)
        {
            return new BinaryNode(tag, new Dictionary<string, string>(), content);
        }

        private static BinaryNode Leaf(string tag, string content)
        {
            return new BinaryNode(tag, new Dictionary<string, string>(), content);
        }

        private static byte[] RequireBytes(BinaryNode parent, string childTag)
        {
            var child = parent != null ? parent.GetChild(childTag) : null;
            var bytes = child != null ? child.GetContentBytes() : null;
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("Missing " + childTag + " in link_code_companion_reg");
            }

            return bytes;
        }

        private static byte[] Slice(byte[] source, int offset, int length)
        {
            var copy = new byte[length];
            Buffer.BlockCopy(source, offset, copy, 0, length);
            return copy;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var total = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] != null)
                {
                    total += parts[i].Length;
                }
            }

            var result = new byte[total];
            var offset = 0;
            for (var i = 0; i < parts.Length; i++)
            {
                if (parts[i] == null || parts[i].Length == 0)
                {
                    continue;
                }

                Buffer.BlockCopy(parts[i], 0, result, offset, parts[i].Length);
                offset += parts[i].Length;
            }

            return result;
        }

        /// <summary>rc14 bytesToCrockford: 5 random bytes become the 8-character pairing code.</summary>
        private static string BytesToCrockford(byte[] bytes)
        {
            ulong value = 0;
            var bitCount = 0;
            var builder = new StringBuilder();

            for (var i = 0; i < bytes.Length; i++)
            {
                value = (value << 8) | bytes[i];
                bitCount += 8;

                while (bitCount >= 5)
                {
                    builder.Append(CrockfordAlphabet[(int)((value >> (bitCount - 5)) & 31)]);
                    bitCount -= 5;
                }
            }

            if (bitCount > 0)
            {
                builder.Append(CrockfordAlphabet[(int)((value << (5 - bitCount)) & 31)]);
            }

            return builder.ToString();
        }
    }
}
