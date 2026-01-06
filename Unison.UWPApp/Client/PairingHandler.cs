using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.UWPApp.Crypto;
using Unison.UWPApp.Protocol;
using Google.Protobuf;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Handles the 8-digit pairing code flow for linking as a companion device.
    /// Based on Baileys requestPairingCode and link_code_companion_reg
    /// </summary>
    public class PairingHandler
    {
        private readonly SocketClient _socket;
        private readonly AuthStore _authStore;

        public event EventHandler<string> OnPairingCode;
        public event EventHandler<UserInfo> OnPairingSuccess;
        public event EventHandler<string> OnPairingFailed;

        public PairingHandler(SocketClient socket, AuthStore authStore)
        {
            _socket = socket ?? throw new ArgumentNullException(nameof(socket));
            _authStore = authStore ?? throw new ArgumentNullException(nameof(authStore));
            Debug.WriteLine($"[PairingHandler] Initialized for Socket using AuthState (ObjID: {_socket.Auth.GetHashCode()})");
        }

        /// <summary>
        /// Requests a pairing code for the given phone number.
        /// Returns the 8-digit code that the user should enter on their phone.
        /// </summary>
        public async Task<string> RequestPairingCodeAsync(string phoneNumber, string customCode = null)
        {
            Debug.WriteLine($"[Pairing] Requesting pairing code for: {phoneNumber}");

            // Generate 8-char pairing code
            var pairingCode = customCode ?? GeneratePairingCode();
            
            if (!string.IsNullOrEmpty(customCode) && customCode.Length != 8)
            {
                throw new ArgumentException("Custom pairing code must be exactly 8 characters");
            }

            // Store pairing code in auth state
            _socket.Auth.PairingCode = pairingCode;
            
            // Set up user info
            _socket.Auth.Me = new UserInfo
            {
                Id = WA.JidEncode(phoneNumber.Replace("+", ""), WA.S_WHATSAPP_NET),
                Name = "~"
            };

            // Generate pairing key (encrypted ephemeral public key)
            var pairingKey = await GeneratePairingKeyAsync();

            // Build the companion device registration request
            // Format verified from Baileys trace log
            var node = new BinaryNode("iq", new Dictionary<string, string>
            {
                { "to", WA.S_WHATSAPP_NET },
                { "type", "set" },
                { "id", _socket.GenerateMessageTag() },
                { "xmlns", "md" }
            }, new List<BinaryNode>
            {
                new BinaryNode("link_code_companion_reg", new Dictionary<string, string>
                {
                    { "jid", _socket.Auth.Me.Id },
                    { "stage", "companion_hello" },
                    { "should_show_push_notification", "true" }  // Required per Baileys
                }, new List<BinaryNode>
                {
                    new BinaryNode("link_code_pairing_wrapped_companion_ephemeral_pub", null, pairingKey),
                    new BinaryNode("companion_server_auth_key_pub", null, _socket.Auth.NoiseKey.Public),
                    // Per Baileys: companion_platform_id is string "1", not raw byte
                    new BinaryNode("companion_platform_id", null, System.Text.Encoding.UTF8.GetBytes("1")),
                    new BinaryNode("companion_platform_display", null, System.Text.Encoding.UTF8.GetBytes("Chrome (Mac OS)")),
                    // Per Baileys: link_code_pairing_nonce is string "0", not raw byte
                    new BinaryNode("link_code_pairing_nonce", null, System.Text.Encoding.UTF8.GetBytes("0"))
                })
            });

            Debug.WriteLine($"[Pairing] Sending companion registration request...");

            try
            {
                var response = await _socket.QueryAsync(node, 30000);
                
                if (response == null)
                {
                    throw new Exception("No response from server");
                }

                Debug.WriteLine($"[Pairing] Received response: {response.Tag}");

                // Check for error
                if (response.Attrs.TryGetValue("type", out var type) && type == "error")
                {
                    var errorNode = response.GetChild("error");
                    var errorCode = errorNode?.Attrs.GetValueOrDefault("code", "unknown");
                    throw new Exception($"Server error: {errorCode}");
                }

                // Save state
                await _authStore.SaveAsync(_socket.Auth);

                Debug.WriteLine($"[Pairing] Pairing code generated: {pairingCode}");
                OnPairingCode?.Invoke(this, pairingCode);

                return pairingCode;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pairing] Request failed: {ex.Message}");
                OnPairingFailed?.Invoke(this, ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Generates a random 8-character Crockford Base32 pairing code
        /// </summary>
        private string GeneratePairingCode()
        {
            // Generate 5 random bytes → 8 chars in Crockford
            var bytes = CryptoUtils.RandomBytes(5);
            return CryptoUtils.BytesToCrockford(bytes).Substring(0, 8);
        }

        /// <summary>
        /// Generates the encrypted pairing key from ephemeral public key
        /// </summary>
        private async Task<byte[]> GeneratePairingKeyAsync()
        {
            var salt = CryptoUtils.RandomBytes(32);
            var iv = CryptoUtils.RandomBytes(16);
            
            // Derive key from pairing code
            var key = CryptoUtils.DerivePairingCodeKey(_socket.Auth.PairingCode, salt);
            
            // Encrypt ephemeral public key with AES-CTR
            var ciphered = CryptoUtils.AesCtrEncrypt(
                _socket.Auth.PairingEphemeralKeyPair.Public, 
                key, 
                iv
            );

            // Concatenate: salt (32) + iv (16) + ciphertext (32)
            var result = new byte[salt.Length + iv.Length + ciphered.Length];
            Array.Copy(salt, 0, result, 0, salt.Length);
            Array.Copy(iv, 0, result, salt.Length, iv.Length);
            Array.Copy(ciphered, 0, result, salt.Length + iv.Length, ciphered.Length);

            await Task.CompletedTask;
            return result;
        }


        /// <summary>
        /// Gets the platform ID for Chrome (from Baileys getPlatformId)
        /// </summary>
        private byte[] GetPlatformId()
        {
            // Platform ID 1 = Chrome (from DeviceProps.PlatformType.CHROME)
            return new byte[] { 1 };
        }

        /// <summary>
        /// Handles the link_code_companion_reg notification sent by the server
        /// when the user enters the pairing code on their phone.
        /// This is the companion_finish stage per Baileys messages-recv.ts lines 805-873.
        /// </summary>
        public async Task HandleLinkCodeCompanionRegAsync(BinaryNode node)
        {
            Debug.WriteLine("[Pairing] Received link_code_companion_reg notification!");

            try
            {
                var regNode = node.GetChild("link_code_companion_reg");
                if (regNode == null)
                {
                    Debug.WriteLine("[Pairing] No link_code_companion_reg child found");
                    return;
                }

                // Extract the necessary data from the notification
                var refNode = regNode.GetChild("link_code_pairing_ref");
                var primaryIdentityNode = regNode.GetChild("primary_identity_pub");
                var wrappedEphemeralNode = regNode.GetChild("link_code_pairing_wrapped_primary_ephemeral_pub");

                if (refNode == null || primaryIdentityNode == null || wrappedEphemeralNode == null)
                {
                    Debug.WriteLine("[Pairing] Missing required nodes in notification");
                    return;
                }

                var refBytes = refNode.Content as byte[];
                var primaryIdentityPub = primaryIdentityNode.Content as byte[];
                var wrappedEphemeral = wrappedEphemeralNode.Content as byte[];

                if (refBytes == null || primaryIdentityPub == null || wrappedEphemeral == null)
                {
                    Debug.WriteLine("[Pairing] Missing content in notification nodes");
                    return;
                }

                Debug.WriteLine($"[Pairing] ref={refBytes.Length}b, primaryIdentity={primaryIdentityPub.Length}b, wrappedEphemeral={wrappedEphemeral.Length}b");

                // Decrypt the wrapped primary ephemeral key using pairing code
                var codePairingPublicKey = DecipherLinkPublicKey(wrappedEphemeral);
                Debug.WriteLine($"[Pairing] Decrypted primary ephemeral: {codePairingPublicKey.Length} bytes");

                // Compute shared key: ECDH(pairingEphemeralKeyPair.private, decrypted primary ephemeral)
                var companionSharedKey = CryptoUtils.SharedKey(
                    _socket.Auth.PairingEphemeralKeyPair.Private,
                    codePairingPublicKey
                );
                Debug.WriteLine($"[Pairing] Companion shared key computed: {companionSharedKey.Length} bytes");

                // Generate random values
                var random = CryptoUtils.RandomBytes(32);
                var linkCodeSalt = CryptoUtils.RandomBytes(32);

                // Derive encryption key using HKDF
                var linkCodePairingExpanded = CryptoUtils.Hkdf(
                    companionSharedKey, 
                    32, 
                    linkCodeSalt, 
                    "link_code_pairing_key_bundle_encryption_key"
                );

                // Build encryption payload: signedIdentityKey.public (33b) + primaryIdentityPub + random
                // NOTE: Baileys uses prefixed public keys (0x05 + 32 bytes = 33 bytes)
                var signedIdentityPubPrefixed = CryptoUtils.GenerateSignalPubKey(_socket.Auth.SignedIdentityKey.Public);
                var encryptPayload = new byte[signedIdentityPubPrefixed.Length + primaryIdentityPub.Length + 32];
                Array.Copy(signedIdentityPubPrefixed, 0, encryptPayload, 0, signedIdentityPubPrefixed.Length);
                Array.Copy(primaryIdentityPub, 0, encryptPayload, signedIdentityPubPrefixed.Length, primaryIdentityPub.Length);
                Array.Copy(random, 0, encryptPayload, signedIdentityPubPrefixed.Length + primaryIdentityPub.Length, 32);

                // Encrypt with AES-GCM
                var encryptIv = CryptoUtils.RandomBytes(12);
                var encrypted = CryptoUtils.AesGcmEncrypt(encryptPayload, linkCodePairingExpanded, encryptIv, new byte[0]);

                // Build encrypted payload: linkCodeSalt + encryptIv + encrypted
                var encryptedPayload = new byte[linkCodeSalt.Length + encryptIv.Length + encrypted.Length];
                Array.Copy(linkCodeSalt, 0, encryptedPayload, 0, linkCodeSalt.Length);
                Array.Copy(encryptIv, 0, encryptedPayload, linkCodeSalt.Length, encryptIv.Length);
                Array.Copy(encrypted, 0, encryptedPayload, linkCodeSalt.Length + encryptIv.Length, encrypted.Length);

                // Compute identity shared key: ECDH(signedIdentityKey.private, primaryIdentityPub)
                var identitySharedKey = CryptoUtils.SharedKey(
                    _socket.Auth.SignedIdentityKey.Private,
                    primaryIdentityPub
                );

                // Build identity payload: companionSharedKey + identitySharedKey + random
                var identityPayload = new byte[companionSharedKey.Length + identitySharedKey.Length + random.Length];
                Array.Copy(companionSharedKey, 0, identityPayload, 0, companionSharedKey.Length);
                Array.Copy(identitySharedKey, 0, identityPayload, companionSharedKey.Length, identitySharedKey.Length);
                Array.Copy(random, 0, identityPayload, companionSharedKey.Length + identitySharedKey.Length, random.Length);

                // Derive advSecretKey using HKDF
                var advSecretKey = CryptoUtils.Hkdf(identityPayload, 32, null, "adv_secret");
                _socket.Auth.AdvSecretKey = Convert.ToBase64String(advSecretKey);
                Debug.WriteLine($"[Pairing] AdvSecretKey derived");

                // Send companion_finish response
                var finishNode = new BinaryNode("iq", new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "set" },
                    { "id", _socket.GenerateMessageTag() },
                    { "xmlns", "md" }
                }, new List<BinaryNode>
                {
                    new BinaryNode("link_code_companion_reg", new Dictionary<string, string>
                    {
                        { "jid", _socket.Auth.Me.Id },
                        { "stage", "companion_finish" }
                    }, new List<BinaryNode>
                    {
                        new BinaryNode("link_code_pairing_wrapped_key_bundle", null, encryptedPayload),
                        new BinaryNode("companion_identity_public", null, signedIdentityPubPrefixed),
                        new BinaryNode("link_code_pairing_ref", null, refBytes)
                    })
                });

                Debug.WriteLine("[Pairing] Sending companion_finish response...");
                await _socket.SendNodeAsync(finishNode);

                // Mark as registered
                _socket.Auth.Registered = true;
                await _authStore.SaveAsync(_socket.Auth);

                Debug.WriteLine("[Pairing] companion_finish sent successfully!");
                OnPairingSuccess?.Invoke(this, _socket.Auth.Me);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pairing] Error in HandleLinkCodeCompanionRegAsync: {ex.Message}");
                Debug.WriteLine($"[Pairing] Stack: {ex.StackTrace}");
                OnPairingFailed?.Invoke(this, ex.Message);
            }
        }

        /// <summary>
        /// Decrypts the wrapped primary ephemeral public key using the pairing code.
        /// Format: salt (32) + iv (16) + encrypted (32) = 80 bytes
        /// </summary>
        private byte[] DecipherLinkPublicKey(byte[] data)
        {
            if (data.Length != 80)
            {
                throw new ArgumentException($"Invalid wrapped key length: {data.Length}, expected 80");
            }

            var salt = new byte[32];
            var iv = new byte[16];
            var payload = new byte[32];

            Array.Copy(data, 0, salt, 0, 32);
            Array.Copy(data, 32, iv, 0, 16);
            Array.Copy(data, 48, payload, 0, 32);

            var secretKey = CryptoUtils.DerivePairingCodeKey(_socket.Auth.PairingCode, salt);
            return CryptoUtils.AesCtrDecrypt(payload, secretKey, iv);
        }

        /// <summary>
        /// Handles pair-success notification from server.
        /// Based on Baileys configureSuccessfulPairing in validate-connection.ts
        /// </summary>
        public async Task HandlePairSuccessAsync(BinaryNode stanza)
        {
            Debug.WriteLine("[Pairing] Received pair-success!");

            try
            {
                var pairSuccessNode = stanza.GetChild("pair-success");
                if (pairSuccessNode == null)
                {
                    throw new Exception("Missing pair-success node");
                }

                // Extract nodes
                var deviceIdentityNode = pairSuccessNode.GetChild("device-identity");
                var platformNode = pairSuccessNode.GetChild("platform");
                var deviceNode = pairSuccessNode.GetChild("device");
                var bizNode = pairSuccessNode.GetChild("biz");

                // Get JID from device node
                var jid = deviceNode?.Attrs.GetValueOrDefault("jid", null);
                if (string.IsNullOrEmpty(jid))
                {
                    throw new Exception("Missing JID in pair-success");
                }

                // Parse device identity bytes
                var deviceIdentityBytes = deviceIdentityNode?.GetContentBytes();
                if (deviceIdentityBytes == null)
                {
                    throw new Exception("Missing device-identity content");
                }

                // Step 1: Parse ADVSignedDeviceIdentityHMAC from phone
                var signedIdentityHmac = Proto.ADVSignedDeviceIdentityHMAC.Parser.ParseFrom(deviceIdentityBytes);
                
                if (!signedIdentityHmac.HasDetails || !signedIdentityHmac.HasHmac)
                {
                    throw new Exception("Missing details or HMAC in device identity");
                }

                var details = signedIdentityHmac.Details.ToByteArray();
                var hmacFromPhone = signedIdentityHmac.Hmac.ToByteArray();

                // Step 2: Verify HMAC using advSecretKey
                var advSecretKey = Convert.FromBase64String(_socket.Auth.AdvSecretKey);
                var calculatedHmac = CryptoUtils.HmacSha256(details, advSecretKey);
                
                if (!CompareBytes(hmacFromPhone, calculatedHmac))
                {
                    throw new Exception("Invalid account signature - HMAC verification failed");
                }
                Debug.WriteLine("[Pairing] HMAC verification passed");

                // Step 3: Parse ADVSignedDeviceIdentity from the verified details
                var account = Proto.ADVSignedDeviceIdentity.Parser.ParseFrom(details);
                
                if (!account.HasAccountSignatureKey || !account.HasAccountSignature || !account.HasDetails)
                {
                    throw new Exception("Missing fields in ADVSignedDeviceIdentity");
                }

                var accountSignatureKey = account.AccountSignatureKey.ToByteArray();
                var accountSignature = account.AccountSignature.ToByteArray();
                var deviceDetails = account.Details.ToByteArray();

                // Parse ADVDeviceIdentity to get key index
                var deviceIdentity = Proto.ADVDeviceIdentity.Parser.ParseFrom(deviceDetails);
                uint keyIndex = deviceIdentity.HasKeyIndex ? deviceIdentity.KeyIndex : 0u;

                // Step 4: Verify account signature
                // accountMsg = WA_ADV_ACCOUNT_SIG_PREFIX + deviceDetails + signedIdentityKey.public
                var accountMsg = ConcatBytes(
                    WA_ADV_ACCOUNT_SIG_PREFIX,
                    deviceDetails,
                    _socket.Auth.SignedIdentityKey.Public
                );

                if (!CryptoUtils.Verify(accountSignatureKey, accountMsg, accountSignature))
                {
                    throw new Exception("Failed to verify account signature");
                }
                Debug.WriteLine("[Pairing] Account signature verification passed");

                // Step 5: Create device signature
                // deviceMsg = WA_ADV_DEVICE_SIG_PREFIX + deviceDetails + signedIdentityKey.public + accountSignatureKey
                var deviceMsg = ConcatBytes(
                    WA_ADV_DEVICE_SIG_PREFIX,
                    deviceDetails,
                    _socket.Auth.SignedIdentityKey.Public,
                    accountSignatureKey
                );

                var deviceSignature = CryptoUtils.Sign(_socket.Auth.SignedIdentityKey.Private, deviceMsg);
                Debug.WriteLine($"[Pairing] Created device signature: {deviceSignature.Length} bytes");

                // Step 6: Build the response ADVSignedDeviceIdentity
                var responseAccount = new Proto.ADVSignedDeviceIdentity
                {
                    Details = account.Details,
                    AccountSignatureKey = account.AccountSignatureKey,
                    AccountSignature = account.AccountSignature,
                    DeviceSignature = ByteString.CopyFrom(deviceSignature)
                };

                // Encode but exclude accountSignatureKey per Baileys encodeSignedDeviceIdentity
                var accountEnc = EncodeSignedDeviceIdentity(responseAccount, false);

                // Send confirmation
                var reply = new BinaryNode("iq", new Dictionary<string, string>
                {
                    { "to", WA.S_WHATSAPP_NET },
                    { "type", "result" },
                    { "id", stanza.Attrs["id"] }
                }, new List<BinaryNode>
                {
                    new BinaryNode("pair-device-sign", null, new List<BinaryNode>
                    {
                        new BinaryNode("device-identity", new Dictionary<string, string>
                        {
                            { "key-index", keyIndex.ToString() }
                        }, accountEnc)
                    })
                });

                await _socket.SendNodeAsync(reply);
                Debug.WriteLine("[Pairing] Sent pair-device-sign confirmation");

                // Update auth state
                string normalizedJid = WA.NormalizeDeviceJid(jid);

                _socket.Auth.Me = new UserInfo
                {
                    Id = normalizedJid,
                    Name = bizNode?.Attrs.GetValueOrDefault("name", "~")
                };
                _socket.Auth.Registered = true;

                // Save state
                await _authStore.SaveAsync(_socket.Auth);

                Debug.WriteLine($"[Pairing] Successfully paired as: {jid}");
                OnPairingSuccess?.Invoke(this, _socket.Auth.Me);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Pairing] Failed to process pair-success: {ex.Message}");
                Debug.WriteLine($"[Pairing] Stack: {ex.StackTrace}");
                OnPairingFailed?.Invoke(this, ex.Message);
                throw;
            }
        }

        // Signature prefixes per Baileys Defaults/index.ts
        private static readonly byte[] WA_ADV_ACCOUNT_SIG_PREFIX = { 6, 0 };
        private static readonly byte[] WA_ADV_DEVICE_SIG_PREFIX = { 6, 1 };

        /// <summary>
        /// Encodes ADVSignedDeviceIdentity, optionally excluding accountSignatureKey
        /// </summary>
        private byte[] EncodeSignedDeviceIdentity(Proto.ADVSignedDeviceIdentity account, bool includeSignatureKey)
        {
            if (!includeSignatureKey || account.AccountSignatureKey?.Length == 0)
            {
                // Create a copy without the accountSignatureKey
                var copy = new Proto.ADVSignedDeviceIdentity
                {
                    Details = account.Details,
                    AccountSignature = account.AccountSignature,
                    DeviceSignature = account.DeviceSignature
                    // Intentionally omit AccountSignatureKey
                };
                return copy.ToByteArray();
            }
            return account.ToByteArray();
        }

        /// <summary>
        /// Helper to concatenate multiple byte arrays
        /// </summary>
        private byte[] ConcatBytes(params byte[][] arrays)
        {
            int totalLen = 0;
            foreach (var arr in arrays) totalLen += arr.Length;
            
            var result = new byte[totalLen];
            int offset = 0;
            foreach (var arr in arrays)
            {
                Array.Copy(arr, 0, result, offset, arr.Length);
                offset += arr.Length;
            }
            return result;
        }

        /// <summary>
        /// Constant-time comparison of byte arrays
        /// </summary>
        private bool CompareBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
                diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }

    /// <summary>
    /// Extension methods for dictionary
    /// </summary>
    public static class DictionaryExtensions
    {
        public static TValue GetValueOrDefault<TKey, TValue>(
            this IDictionary<TKey, TValue> dictionary, 
            TKey key, 
            TValue defaultValue = default(TValue))
        {
            return dictionary.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
}
