// =============================================================================
// PairingConfigurator
//
// The cryptographic core of pairing: verifies the HMAC and the account signature
// the phone sent, then countersigns the identity with our own key so WhatsApp
// accepts this device as a companion.
//
// It is a pure function over the stanza and the credentials - it neither sends
// nor persists anything - which makes the entire verification testable with a
// captured stanza and no socket at all.
//
// Ports: rc14 configureSuccessfulPairing / encodeSignedDeviceIdentity in
//        src/Utils/validate-connection.ts
// =============================================================================
using System;
using System.Collections.Generic;
using Google.Protobuf;
using Unison.Baileys.Client;
using Unison.Baileys.Crypto;
using Unison.Baileys.Protocol;

namespace Unison.Socket.Session.Pairing
{
    /// <summary>
    /// Everything a successful pairing produced: the node to send back, plus the credentials
    /// the host must persist before the server forces a reconnect.
    /// </summary>
    public sealed class PairingResult
    {
        /// <summary>The pair-device-sign node to send back, acknowledging the link.</summary>
        public BinaryNode Reply { get; set; }

        public UserInfo Me { get; set; }

        public AccountInfo Account { get; set; }

        /// <summary>Phone-reported platform name, e.g. "iphone" or "android".</summary>
        public string Platform { get; set; }

        /// <summary>Account signature key of the primary device, for the Signal identity store.</summary>
        public byte[] AccountSignatureKey { get; set; }
    }

    /// <summary>
    /// Verifies the identity the phone sent in pair-success and countersigns it.
    /// </summary>
    /// <remarks>
    /// Pure function over the stanza and the credentials: it neither sends nor persists anything,
    /// which is what makes the whole pairing verification testable without a socket.
    /// </remarks>
    public static class PairingConfigurator
    {
        public static PairingResult Configure(BinaryNode stanza, AuthState auth)
        {
            if (stanza == null)
            {
                throw new ArgumentNullException(nameof(stanza));
            }

            if (auth == null)
            {
                throw new ArgumentNullException(nameof(auth));
            }

            var pairSuccess = stanza.GetChild("pair-success");
            if (pairSuccess == null)
            {
                throw new WaConnectionException("Missing pair-success node", DisconnectReason.BadSession);
            }

            var deviceIdentityNode = pairSuccess.GetChild("device-identity");
            var deviceNode = pairSuccess.GetChild("device");
            if (deviceIdentityNode == null || deviceNode == null)
            {
                throw new WaConnectionException(
                    "Missing device-identity or device in pair success node", DisconnectReason.BadSession);
            }

            var platformNode = pairSuccess.GetChild("platform");
            var businessNode = pairSuccess.GetChild("biz");

            var signedHmac = global::Proto.ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(
                deviceIdentityNode.GetContentBytes());

            var details = signedHmac.Details.ToByteArray();
            var advSecretKey = Convert.FromBase64String(auth.AdvSecretKey);

            // Hosted (business) accounts prefix the HMAC input; missing this rejects a valid link.
            var hmacPrefix = signedHmac.HasAccountType && signedHmac.AccountType == global::Proto.ADVEncryptionType.Hosted
                ? AdvConstants.HostedAccountSigPrefix
                : new byte[0];

            var expectedHmac = CryptoUtils.HmacSha256(Concat(hmacPrefix, details), advSecretKey);
            if (!ConstantTimeEquals(signedHmac.Hmac.ToByteArray(), expectedHmac))
            {
                throw new WaConnectionException("Invalid account signature", DisconnectReason.BadSession);
            }

            var account = global::Proto.ADVSignedDeviceIdentity.Parser.ParseFrom(details);
            var deviceDetails = account.Details.ToByteArray();
            var accountSignatureKey = account.AccountSignatureKey.ToByteArray();

            var deviceIdentity = global::Proto.ADVDeviceIdentity.Parser.ParseFrom(deviceDetails);

            var accountSigPrefix = deviceIdentity.HasDeviceType
                && deviceIdentity.DeviceType == global::Proto.ADVEncryptionType.Hosted
                ? AdvConstants.HostedAccountSigPrefix
                : AdvConstants.AccountSigPrefix;

            var accountMsg = Concat(accountSigPrefix, deviceDetails, auth.SignedIdentityKey.Public);
            if (!CryptoUtils.Verify(accountSignatureKey, accountMsg, account.AccountSignature.ToByteArray()))
            {
                throw new WaConnectionException("Failed to verify account signature", DisconnectReason.BadSession);
            }

            var deviceMsg = Concat(
                AdvConstants.DeviceSigPrefix,
                deviceDetails,
                auth.SignedIdentityKey.Public,
                accountSignatureKey);

            var deviceSignature = CryptoUtils.Sign(auth.SignedIdentityKey.Private, deviceMsg);
            account.DeviceSignature = ByteString.CopyFrom(deviceSignature);

            var keyIndex = deviceIdentity.HasKeyIndex ? deviceIdentity.KeyIndex : 0u;
            var accountEnc = EncodeSignedDeviceIdentity(account, false);

            var reply = new BinaryNode(
                "iq",
                new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "result" },
                    { "id", stanza.GetAttribute("id") }
                },
                new List<BinaryNode>
                {
                    new BinaryNode(
                        "pair-device-sign",
                        null,
                        new List<BinaryNode>
                        {
                            new BinaryNode(
                                "device-identity",
                                new Dictionary<string, string> { { "key-index", keyIndex.ToString() } },
                                accountEnc)
                        })
                });

            return new PairingResult
            {
                Reply = reply,
                Me = new UserInfo
                {
                    Id = deviceNode.GetAttribute("jid"),
                    Lid = deviceNode.GetAttribute("lid"),
                    Name = businessNode != null ? businessNode.GetAttribute("name") : null
                },
                Account = new AccountInfo
                {
                    Details = deviceDetails,
                    AccountSignatureKey = accountSignatureKey,
                    AccountSignature = account.AccountSignature.ToByteArray(),
                    DeviceSignature = deviceSignature
                },
                Platform = platformNode != null ? platformNode.GetAttribute("name") : null,
                AccountSignatureKey = accountSignatureKey
            };
        }

        /// <summary>
        /// The signature key is dropped unless explicitly requested: the server already has it,
        /// and echoing it back makes WhatsApp reject the node.
        /// </summary>
        public static byte[] EncodeSignedDeviceIdentity(
            global::Proto.ADVSignedDeviceIdentity account, bool includeSignatureKey)
        {
            var clone = account.Clone();

            if (!includeSignatureKey || clone.AccountSignatureKey.Length == 0)
            {
                clone.ClearAccountSignatureKey();
            }

            return clone.ToByteArray();
        }

        private static byte[] Concat(params byte[][] parts)
        {
            var length = 0;
            foreach (var part in parts)
            {
                if (part != null)
                {
                    length += part.Length;
                }
            }

            var result = new byte[length];
            var offset = 0;
            foreach (var part in parts)
            {
                if (part != null && part.Length > 0)
                {
                    Buffer.BlockCopy(part, 0, result, offset, part.Length);
                    offset += part.Length;
                }
            }

            return result;
        }

        private static bool ConstantTimeEquals(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length)
            {
                return false;
            }

            var diff = 0;
            for (var i = 0; i < a.Length; i++)
            {
                diff |= a[i] ^ b[i];
            }

            return diff == 0;
        }
    }
}
