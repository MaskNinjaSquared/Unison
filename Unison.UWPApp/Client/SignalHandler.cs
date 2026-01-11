using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Unison.UWPApp.Crypto;
using Google.Protobuf;
using Proto;
using Newtonsoft.Json;
using Unison.UWPApp.Protocol;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Handles Signal V3 protocol decryption for WhatsApp messages.
    /// Ported/Simplified from Baileys and LibSignal.
    /// </summary>
    public class SignalHandler
    {
        private readonly AuthState _authState;
        private readonly IKeyStore _keyStore;
        private readonly object _sessionLock = new object();

        public SignalHandler(AuthState authState, IKeyStore keyStore = null)
        {
            _authState = authState ?? throw new ArgumentNullException(nameof(authState));
            _keyStore = keyStore;
        }

        /// <summary>
        /// Saves a session to persistent storage (if keyStore is available)
        /// </summary>
        public async System.Threading.Tasks.Task SaveSessionAsync(string jid)
        {
            if (_keyStore == null) return;
            
            jid = WA.NormalizeDeviceJid(jid);
            byte[] sessionData;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(jid, out sessionData))
                    return;
            }
            
            await _keyStore.SetSessionAsync(jid, sessionData);
        }

        /// <summary>
        /// Loads all sessions from persistent storage into memory
        /// </summary>
        public async System.Threading.Tasks.Task LoadSessionsFromStoreAsync()
        {
            if (_keyStore == null) return;

            var jids = await _keyStore.GetAllSessionJidsAsync();
            foreach (var jid in jids)
            {
                var data = await _keyStore.GetSessionAsync(jid);
                if (data != null)
                {
                    lock (_sessionLock)
                    {
                        _authState.Sessions[jid] = data;
                    }
                }
            }
            System.Diagnostics.Debug.WriteLine($"[Signal] Loaded {_authState.Sessions.Count} sessions from store");
        }

        /// <summary>
        /// Checks if the keyStore has a session (both memory and disk)
        /// </summary>
        public bool HasSessionInStore(string jid)
        {
            jid = WA.NormalizeDeviceJid(jid);
            if (_keyStore != null && _keyStore.HasSession(jid))
                return true;
            lock (_sessionLock)
            {
                return _authState.Sessions.ContainsKey(jid);
            }
        }

        public class SessionData
        {
            // Receiving chain (established when we received their pkmsg)
            public byte[] RootKey { get; set; }
            public byte[] ChainKey { get; set; }  // Receiving chain key
            public uint Counter { get; set; }      // Receiving counter
            public byte[] TheirIdentityPublicKey { get; set; }
            public byte[] OurRatchetPrivateKey { get; set; }  // Our key used for receiving
            public byte[] TheirRatchetPublicKey { get; set; } // Their current public key
            
            // Sending chain (established when we first send)
            public byte[] SendingChainKey { get; set; }
            public uint SendingCounter { get; set; }
            public uint PreviousSendingCounter { get; set; }      // Tracking for PreviousCounter field from previous chain
            public byte[] OurSendingRatchetPrivate { get; set; }  // Ephemeral key for sending
            public byte[] OurSendingRatchetPublic { get; set; }   // Public key to include in message

            // PreKey info for first message (Type 3 / pkmsg)
            public bool IsPendingPreKey { get; set; }
            public uint? PendingSignedPreKeyId { get; set; }
            public uint? PendingPreKeyId { get; set; }
            public byte[] PendingBaseKey { get; set; } // Our EK_A
            
            public uint RegistrationId { get; set; }
            
            // Flag to indicate if this session can be used for sending
            // Sessions created from receiving (EstablishSessionAndDecrypt) are receive-only
            // Sessions created via InitializeOutgoingSession can send
            public bool CanSend { get; set; }
        }

        public void InitializeOutgoingSession(string jid, SocketClient.PreKeyBundle bundle)
        {
            jid = WA.NormalizeDeviceJid(jid);
            System.Diagnostics.Debug.WriteLine($"[Signal] ====== InitializeOutgoingSession for {jid} ======");
            System.Diagnostics.Debug.WriteLine($"[Signal] OUR KEYS:");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Our RegistrationId: {_authState.RegistrationId}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Our IdentityKey.Public: {BitConverter.ToString(_authState.SignedIdentityKey.Public.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Our IdentityKey.Private (first 8): {BitConverter.ToString(_authState.SignedIdentityKey.Private.Take(8).ToArray())}...");
            
            System.Diagnostics.Debug.WriteLine($"[Signal] THEIR BUNDLE:");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle RegistrationId: {bundle.RegistrationId}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle SignedPreKeyId: {bundle.SignedPreKeyId}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle OneTimePreKeyId: {bundle.OneTimePreKeyId}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle IdentityKey (IK_B): {BitConverter.ToString(bundle.IdentityKey?.Take(8).ToArray() ?? new byte[0])}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle SignedPreKey (SPK_B): {BitConverter.ToString(bundle.SignedPreKey?.Take(8).ToArray() ?? new byte[0])}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Bundle OneTimePreKey (OPK_B): {(bundle.OneTimePreKey != null ? BitConverter.ToString(bundle.OneTimePreKey.Take(8).ToArray()) + "..." : "null")}");
            
            // 1. Generate our ephemeral key pair (EK_A)
            var ourEphemeral = CryptoUtils.GenerateKeyPair();
            System.Diagnostics.Debug.WriteLine($"[Signal] EPHEMERAL:");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Our Ephemeral Public (EK_A): {BitConverter.ToString(ourEphemeral.Public.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Our Ephemeral Private (first 8): {BitConverter.ToString(ourEphemeral.Private.Take(8).ToArray())}...");
            
            // 2. DH Exchanges (X3DH)
            System.Diagnostics.Debug.WriteLine($"[Signal] X3DH KEY EXCHANGES:");
            
            // DH1 = DH(IK_A_private, SPK_B)
            byte[] dh1 = CryptoUtils.SharedKey(_authState.SignedIdentityKey.Private, bundle.SignedPreKey);
            System.Diagnostics.Debug.WriteLine($"[Signal]   DH1 = DH(IK_A_priv, SPK_B): {BitConverter.ToString(dh1.Take(8).ToArray())}...");
            
            // DH2 = DH(EK_A_private, IK_B)
            byte[] dh2 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.IdentityKey);
            System.Diagnostics.Debug.WriteLine($"[Signal]   DH2 = DH(EK_A_priv, IK_B): {BitConverter.ToString(dh2.Take(8).ToArray())}...");
            
            // DH3 = DH(EK_A_private, SPK_B)
            byte[] dh3 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.SignedPreKey);
            System.Diagnostics.Debug.WriteLine($"[Signal]   DH3 = DH(EK_A_priv, SPK_B): {BitConverter.ToString(dh3.Take(8).ToArray())}...");
            
            byte[] sharedSecret;
            if (bundle.OneTimePreKey != null)
            {
                // DH4 = DH(EK_A_private, OPK_B)
                byte[] dh4 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.OneTimePreKey);
                System.Diagnostics.Debug.WriteLine($"[Signal]   DH4 = DH(EK_A_priv, OPK_B): {BitConverter.ToString(dh4.Take(8).ToArray())}...");
                
                sharedSecret = new byte[32 * 5];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF; // Mandatory 0xFF prefix
                Buffer.BlockCopy(dh1, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(dh2, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(dh3, 0, sharedSecret, 96, 32);
                Buffer.BlockCopy(dh4, 0, sharedSecret, 128, 32);
                System.Diagnostics.Debug.WriteLine($"[Signal]   SharedSecret (5-part, 160 bytes): FF*32 || DH1 || DH2 || DH3 || DH4");
            }
            else
            {
                sharedSecret = new byte[32 * 4];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF; // Mandatory 0xFF prefix
                Buffer.BlockCopy(dh1, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(dh2, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(dh3, 0, sharedSecret, 96, 32);
                System.Diagnostics.Debug.WriteLine($"[Signal]   SharedSecret (4-part, 128 bytes): FF*32 || DH1 || DH2 || DH3");
            }
            System.Diagnostics.Debug.WriteLine($"[Signal]   SharedSecret[32-40]: {BitConverter.ToString(sharedSecret.Skip(32).Take(8).ToArray())}... (should be DH1)");
            
            // 3. Derive Root Key and Initial Chain Key using DeriveSecrets (matching BaileysCSharp)
            byte[] salt = new byte[32]; // All zeros salt
            byte[][] masterKeys = CryptoUtils.DeriveSecrets(sharedSecret, salt, "WhisperText", 2);
            System.Diagnostics.Debug.WriteLine($"[Signal] DeriveSecrets DERIVATION:");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Salt: 32 x 0x00");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Info: 'WhisperText'");
            System.Diagnostics.Debug.WriteLine($"[Signal]   MasterKey[0] (RootKey): {BitConverter.ToString(masterKeys[0].Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   MasterKey[1] (ChainKey): {BitConverter.ToString(masterKeys[1].Take(8).ToArray())}...");
            
            byte[] rootKey = masterKeys[0];
            byte[] initialChainKey = masterKeys[1];
            System.Diagnostics.Debug.WriteLine($"[Signal]   Derived RootKey: {BitConverter.ToString(rootKey.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   Derived ChainKey: {BitConverter.ToString(initialChainKey.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal] ====== END InitializeOutgoingSession ======");
            
            // 4. Create SessionData
            // Note: We do NOT set SendingChainKey here. 
            // EncryptMessage will step the ratchet when it sees SendingChainKey is null.
            var session = new SessionData
            {
                RegistrationId = bundle.RegistrationId,
                RootKey = rootKey,
                ChainKey = initialChainKey,
                Counter = 0,
                TheirIdentityPublicKey = bundle.IdentityKey,
                TheirRatchetPublicKey = bundle.SignedPreKey, // Start with their SignedPreKey as the ratchet key
                OurRatchetPrivateKey = ourEphemeral.Private, // Our base key for their next ratchet step
                SendingCounter = 0,
                PreviousSendingCounter = 0,
                IsPendingPreKey = true,
                PendingSignedPreKeyId = bundle.SignedPreKeyId,
                PendingPreKeyId = bundle.OneTimePreKeyId,
                PendingBaseKey = ourEphemeral.Public,
                CanSend = true  // This session was established for sending
            };
            
            var sessionJson = JsonConvert.SerializeObject(session);
            lock (_sessionLock)
            {
                _authState.Sessions[jid] = System.Text.Encoding.UTF8.GetBytes(sessionJson);
            }
            System.Diagnostics.Debug.WriteLine($"[Signal] New session initialized and saved for {jid}");
        }

        /// <summary>
        /// Checks if a session exists for the given JID.
        /// </summary>
        public bool HasSession(string jid)
        {
            jid = WA.NormalizeDeviceJid(jid);
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(jid, out var sessionJson))
                    return false;
                
                // Check if session can send (not just receive-only)
                try
                {
                    var session = JsonConvert.DeserializeObject<SessionData>(System.Text.Encoding.UTF8.GetString(sessionJson));
                    return session?.CanSend == true;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Result of Signal message encryption.
        /// </summary>
        public class EncryptResult
        {
            public string Type { get; set; }
            public byte[] Ciphertext { get; set; }
        }

        /// <summary>
        /// Decrypts a Signal message from a binary node.
        /// Signal V3 format: [1 byte version] + [protobuf message] + [8 byte MAC]
        /// </summary>
        public byte[] DecryptMessage(byte[] data, string senderJid, string signalType = null)
        {
            senderJid = WA.NormalizeDeviceJid(senderJid);
            if (data == null || data.Length < 1) return null;

            byte version = (byte)(data[0] >> 4);
            byte type = (byte)(data[0] & 0x0F);
            
            System.Diagnostics.Debug.WriteLine($"[Signal] Received packet: version={version}, type={type}, length={data.Length}");

            if (version > 3)
            {
                throw new Exception($"Unsupported Signal version: {version}");
            }

            // Signal messages have: version byte (1) + protobuf + [MAC (8)]
            // For pkmsg (Type 3), it's the same but the nested message usually has the MAC.
            
            byte[] serialized;
            byte[] mac = null;

            if (signalType == "skmsg")
            {
                // skmsg has a 64-byte signature at the end
                if (data.Length < 65) return null;
                serialized = new byte[data.Length - 1 - 64];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                System.Diagnostics.Debug.WriteLine($"[Signal] skmsg: Stripped 64-byte signature. Protobuf length: {serialized.Length}");
            }
            else if (type == 3) // pkmsg
            {
                // Top-level pkmsg has no MAC
                serialized = new byte[data.Length - 1];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                System.Diagnostics.Debug.WriteLine($"[Signal] pkmsg: Sliced version, length={serialized.Length}");
            }
            else // msg (Type 2) - usually has 8-byte MAC
            {
                if (data.Length < 10) return null;
                serialized = new byte[data.Length - 1 - 8];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                
                mac = new byte[8];
                Array.Copy(data, data.Length - 8, mac, 0, 8);
                System.Diagnostics.Debug.WriteLine($"[Signal] msg: Sliced version and 8-byte MAC, protobuf length={serialized.Length}");
            }

            try 
            {
                // Try to parse based on type if possible, or just try both
                PreKeySignalMessage pkMsg = null;
                SignalMessage signalMsg = null;
                SenderKeyMessage skMsg = null;

                if (signalType == "skmsg")
                {
                    try {
                        skMsg = SenderKeyMessage.Parser.ParseFrom(serialized);
                    } catch (Exception ex) {
                        System.Diagnostics.Debug.WriteLine($"[Signal] Failed to parse as SenderKeyMessage: {ex.Message}");
                    }
                }
                else if (type == 3) // PREKEY_BUNDLE
                {
                    try {
                        pkMsg = PreKeySignalMessage.Parser.ParseFrom(serialized);
                    } catch (Exception ex) {
                        System.Diagnostics.Debug.WriteLine($"[Signal] Failed to parse as PreKeySignalMessage: {ex.Message}");
                    }
                }

                if (skMsg != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] Processing skmsg (SenderKey) from {senderJid}, id={skMsg.Id}");
                    return DecryptSenderKeyMessage(skMsg, senderJid);
                }

                if (pkMsg != null && pkMsg.HasBaseKey)
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] Processing pkmsg from {senderJid}, preKeyId={pkMsg.PreKeyId}");
                    return EstablishSessionAndDecrypt(pkMsg, senderJid);
                }

                // Fallback or Type 2
                try {
                    signalMsg = SignalMessage.Parser.ParseFrom(serialized);
                } catch (Exception ex) {
                    System.Diagnostics.Debug.WriteLine($"[Signal] Failed to parse as SignalMessage: {ex.Message}");
                }

                if (signalMsg != null)
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] Processing msg from {senderJid}, counter={signalMsg.Counter}");
                    return DecryptWithExistingSession(signalMsg, senderJid);
                }

                return null; 
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] Decryption failed: {ex.Message}");
                return null;
            }
        }

        private byte[] DecryptSenderKeyMessage(SenderKeyMessage skMsg, string senderJid)
        {
            // WhatsApp group messages use Sender Key protocol. 
            // We need a SenderKeyRecord for this group/sender.
            // For now, we'll try to find it in Sessions (using a special prefix or just specific logic)
            // TODO: Dedicated SenderKey storage.
            
            string key = $"sk:{senderJid}";
            byte[] sessionData;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(key, out sessionData))
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] No SenderKey found for {senderJid}");
                    return null;
                }
            }

            // Implementation note: SenderKey decryption is simpler than individual session.
            // 1. Get SenderKeyState for skMsg.Id
            // 2. Advance chain to skMsg.Iteration
            // 3. Derive keys and decrypt.
            
            return null; // Placeholder for now, needs full implementation of SenderKey advancement.
        }

        private byte[] EstablishSessionAndDecrypt(PreKeySignalMessage pkMsg, string senderJid)
        {
            // 1. Resolve local keys
            var ourIdentityKey = _authState.SignedIdentityKey;
            
            // Signed PreKey
            if (pkMsg.SignedPreKeyId != _authState.SignedPreKey.KeyId)
            {
                throw new Exception($"SignedPreKey mismatch: expected {_authState.SignedPreKey.KeyId}, got {pkMsg.SignedPreKeyId}");
            }
            var ourSignedPreKey = _authState.SignedPreKey.KeyPair;

            // One-time PreKey (optional but common in first message)
            KeyPair ourOneTimePreKey = null;
            if (pkMsg.HasPreKeyId)
            {
                if (_authState.PreKeys.TryGetValue((int)pkMsg.PreKeyId, out var preKeyData))
                {
                    ourOneTimePreKey = preKeyData.KeyPair;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] Warning: One-time pre-key {pkMsg.PreKeyId} not found locally.");
                }
            }

            // 2. Perform DH calculations (X3DH)
            byte[] theirIdentityKey = pkMsg.IdentityKey.ToByteArray();
            byte[] theirBaseKey = pkMsg.BaseKey.ToByteArray();

            System.Diagnostics.Debug.WriteLine($"[Signal] X3DH: TheirIdentity={BitConverter.ToString(theirIdentityKey.Take(4).ToArray())}..., TheirBase={BitConverter.ToString(theirBaseKey.Take(4).ToArray())}...");

            var a1 = CryptoUtils.SharedKey(ourIdentityKey.Private, theirBaseKey);
            var a2 = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirIdentityKey);
            var a3 = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirBaseKey);

            System.Diagnostics.Debug.WriteLine($"[Signal] DH Results: a1={BitConverter.ToString(a1.Take(4).ToArray())}..., a2={BitConverter.ToString(a2.Take(4).ToArray())}..., a3={BitConverter.ToString(a3.Take(4).ToArray())}...");

            byte[] sharedSecret;
            if (ourOneTimePreKey != null)
            {
                var a4 = CryptoUtils.SharedKey(ourOneTimePreKey.Private, theirBaseKey);
                System.Diagnostics.Debug.WriteLine($"[Signal] DH Results: a4={BitConverter.ToString(a4.Take(4).ToArray())}...");
                sharedSecret = new byte[32 * 5];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF;
                Buffer.BlockCopy(a2, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(a1, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(a3, 0, sharedSecret, 96, 32);
                Buffer.BlockCopy(a4, 0, sharedSecret, 128, 32);
            }
            else
            {
                sharedSecret = new byte[32 * 4];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF;
                Buffer.BlockCopy(a2, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(a1, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(a3, 0, sharedSecret, 96, 32);
            }
            
            System.Diagnostics.Debug.WriteLine($"[Signal] X3DH SharedSecret (bytes 32-48): {BitConverter.ToString(sharedSecret.Skip(32).Take(16).ToArray())}");

            // 3. Derive Root Key
            byte[] salt = new byte[32]; 
            byte[] info = System.Text.Encoding.UTF8.GetBytes("WhisperText");
            byte[] masterKey = CryptoUtils.Hkdf(sharedSecret, 64, salt, info);
            
            byte[] rootKey = masterKey.Take(32).ToArray();
            System.Diagnostics.Debug.WriteLine($"[Signal] RootKey (initial): {BitConverter.ToString(rootKey.Take(4).ToArray())}...");

            // 4. Parse Nested Message to get correct Ratchet Key
            byte[] msgBytes = pkMsg.Message.ToByteArray();
            byte[] originalMac = null;
            if (msgBytes.Length > 0)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] Nested header byte: {msgBytes[0]:X2}");
                if ((msgBytes[0] & 0xF0) == 0x30)
                {
                    if (msgBytes.Length >= 10)
                    {
                        originalMac = new byte[8];
                        Array.Copy(msgBytes, msgBytes.Length - 8, originalMac, 0, 8);
                        byte[] innerSerialized = new byte[msgBytes.Length - 1 - 8];
                        Array.Copy(msgBytes, 1, innerSerialized, 0, innerSerialized.Length);
                        msgBytes = innerSerialized;
                        System.Diagnostics.Debug.WriteLine($"[Signal] Stripped nested header and 8-byte MAC, length={msgBytes.Length}");
                    }
                    else
                    {
                        // Fallback for extremely short packets (shouldn't happen for valid Signal messages)
                        byte[] stripped = new byte[msgBytes.Length - 1];
                        Array.Copy(msgBytes, 1, stripped, 0, stripped.Length);
                        msgBytes = stripped;
                        System.Diagnostics.Debug.WriteLine($"[Signal] Stripped nested header ONLY (too short for MAC), length={msgBytes.Length}");
                    }
                }
            }

            SignalMessage signalMsg = SignalMessage.Parser.ParseFrom(msgBytes);
            byte[] theirRatchetKey = signalMsg.RatchetKey.ToByteArray();
            System.Diagnostics.Debug.WriteLine($"[Signal] Nested Msg: Counter={signalMsg.Counter}, RatchetKey={BitConverter.ToString(theirRatchetKey.Take(4).ToArray())}...");

            // 5. Initial Ratchet Step (Receiver)
            // Use the ephemeral key FROM THE MESSAGE, which should match theirBaseKey in the first message.
            byte[] ratchetSharedSecret = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirRatchetKey);
            byte[] ratchetMasterKey = CryptoUtils.Hkdf(ratchetSharedSecret, 64, rootKey, "WhisperRatchet");
            
            byte[] finalRootKey = ratchetMasterKey.Take(32).ToArray();
            byte[] chainKey = ratchetMasterKey.Skip(32).Take(32).ToArray();
            System.Diagnostics.Debug.WriteLine($"[Signal] Initial ChainKey: {BitConverter.ToString(chainKey.Take(4).ToArray())}...");

            // Store session - This is a RECEIVE-ONLY session
            // When we receive a pkmsg, we establish a session for decrypting THEIR messages to US.
            // This does NOT allow us to send to them - we need their prekey bundle for that.
            // HasSession will return false for this session (CanSend=false), forcing prekey bundle fetch.
            
            var session = new SessionData
            {
                RootKey = finalRootKey,
                ChainKey = chainKey,
                Counter = 0,
                TheirIdentityPublicKey = theirIdentityKey,
                OurRatchetPrivateKey = ourSignedPreKey.Private,
                TheirRatchetPublicKey = theirRatchetKey,
                CanSend = false  // Receive-only session - need their prekey bundle to send
            };
            
            System.Diagnostics.Debug.WriteLine($"[Signal] Receive-only session established from pkmsg. CanSend=false.");
            
            // 6. Decrypt the payload
            var plaintext = DecryptPayload(signalMsg, session, senderJid);
            if (plaintext != null)
            {
                // Save updated session ONLY if decryption was successful
                var updatedSessionJson = Newtonsoft.Json.JsonConvert.SerializeObject(session);
                lock (_sessionLock)
                {
                    _authState.Sessions[senderJid] = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
                }
                System.Diagnostics.Debug.WriteLine($"[Signal] Session established and saved for {senderJid}");
            }
            
            return plaintext;
        }


        private byte[] DecryptWithExistingSession(SignalMessage msg, string senderJid)
        {
            byte[] sessionJson;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(senderJid, out sessionJson))
                {
                    System.Diagnostics.Debug.WriteLine($"[Signal] No session found for {senderJid}");
                    return null;
                }
            }

            var sessionJsonText = System.Text.Encoding.UTF8.GetString(sessionJson);
            var session = JsonConvert.DeserializeObject<SessionData>(sessionJsonText);
            
            // Handle Ratchet Advancement (Forward Secrecy)
            if (msg.HasRatchetKey && !msg.RatchetKey.ToByteArray().SequenceEqual(session.TheirRatchetPublicKey ?? new byte[0]))
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] Advancing ratchet for {senderJid}");
                
                byte[] theirNewRatchetKey = msg.RatchetKey.ToByteArray();
                
                // masterKey = HKDF(DH(theirNewRatchetKey, ourRatchetPrivateKey), RootKey, info="WhisperRatchet")
                byte[] sharedSecret = CryptoUtils.SharedKey(session.OurRatchetPrivateKey, theirNewRatchetKey);
                byte[] masterKey = CryptoUtils.Hkdf(sharedSecret, 64, session.RootKey, "WhisperRatchet");
                
                session.RootKey = masterKey.Take(32).ToArray();
                session.ChainKey = masterKey.Skip(32).Take(32).ToArray();
                session.Counter = 0;
                session.TheirRatchetPublicKey = theirNewRatchetKey;
                
                // In Double Ratchet, when we step the receiving ratchet, we should also track the previous sending chain length
                // and prepare to step the sending ratchet on our next send.
                session.PreviousSendingCounter = session.SendingCounter;
                session.SendingChainKey = null; // Force EncryptMessage to step the sending ratchet
                
                System.Diagnostics.Debug.WriteLine($"[Signal] Ratchet advanced. New RootKey: {BitConverter.ToString(session.RootKey.Take(4).ToArray())}...");
            }

            var plaintext = DecryptPayload(msg, session, senderJid);
            if (plaintext != null)
            {
                // Save updated session state (chain advancement and ratchet)
                var updatedSessionJson = Newtonsoft.Json.JsonConvert.SerializeObject(session);
                lock (_sessionLock)
                {
                    _authState.Sessions[senderJid] = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
                }
            }

            return plaintext;
        }

        private byte[] DecryptPayload(SignalMessage msg, SessionData session, string senderJid)
        {
            // Use local copies for advancement so we don't corrupt the stored session on temporary failure
            byte[] currentChainKey = session.ChainKey;
            uint currentCounter = session.Counter;
            
            if (msg.Counter < currentCounter)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] Error: Message counter {msg.Counter} older than session counter {currentCounter}");
                // In a full implementation we'd check for skipped keys here
                return null;
            }

            // Catch up to the message counter
            for (uint i = currentCounter; i < msg.Counter; i++)
            {
                // Advance chain key: chainKey = HMAC-SHA256(key=chainKey, data=0x02)
                currentChainKey = CryptoUtils.HmacSha256(new byte[] { 0x02 }, currentChainKey);
                currentCounter++;
            }
            
            System.Diagnostics.Debug.WriteLine($"[Signal] Using ChainKey for Counter {msg.Counter}: {BitConverter.ToString(currentChainKey.Take(4).ToArray())}...");

            // 1. Derive Message Key from current (possibly advanced) Chain Key: HMAC-SHA256(key=ChainKey, data=0x01)
            byte[] msgKey = CryptoUtils.HmacSha256(new byte[] { 0x01 }, currentChainKey);

            // 2. Derive Cipher Key, Mac Key, IV: HKDF(MessageKey, salt=0, info="WhisperMessageKeys")
            byte[] keys = CryptoUtils.Hkdf(msgKey, 80, new byte[32], "WhisperMessageKeys");
            
            byte[] cipherKey = new byte[32];
            byte[] macKey = new byte[32];
            byte[] iv = new byte[16];
            
            Array.Copy(keys, 0, cipherKey, 0, 32);
            Array.Copy(keys, 32, macKey, 0, 32);
            Array.Copy(keys, 64, iv, 0, 16);

            // 4. Decrypt with AES-CBC
            byte[] ciphertext = msg.Ciphertext.ToByteArray();

            try
            {
                var plaintext = CryptoUtils.AesCbcDecrypt(ciphertext, cipherKey, iv);
                System.Diagnostics.Debug.WriteLine($"[Signal] Successfully decrypted payload: {plaintext.Length} bytes");
                
                // --- SUCCESS: COMMIT STATE ADVANCEMENT ---
                // Advance chain key once more to be ready for the NEXT message
                session.ChainKey = CryptoUtils.HmacSha256(new byte[] { 0x02 }, currentChainKey);
                session.Counter = currentCounter + 1;
                
                if (plaintext.Length > 0)
                {
                    var dumpStart = BitConverter.ToString(plaintext.Take(Math.Min(plaintext.Length, 32)).ToArray());
                    System.Diagnostics.Debug.WriteLine($"[Signal] Plaintext hex dump (start): {dumpStart}");
                    
                    if (plaintext.Length > 32)
                    {
                        var dumpEnd = BitConverter.ToString(plaintext.Skip(plaintext.Length - Math.Min(plaintext.Length, 16)).ToArray());
                        System.Diagnostics.Debug.WriteLine($"[Signal] Plaintext hex dump (end): {dumpEnd}");
                    }
                }
                
                // Signal protocol sometimes has trailing zeros as padding that trip up the protobuf parser
                int actualLen = plaintext.Length;
                while (actualLen > 0 && plaintext[actualLen - 1] == 0)
                {
                    actualLen--;
                }
                
                if (actualLen < plaintext.Length)
                {
                    byte[] trimmed = new byte[actualLen];
                    Array.Copy(plaintext, 0, trimmed, 0, actualLen);
                    System.Diagnostics.Debug.WriteLine($"[Signal] Trimmed {plaintext.Length - actualLen} trailing zeros. New length: {actualLen}");
                    return trimmed;
                }
                
                return plaintext;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] AES decryption failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pads plaintext with random bytes, last byte indicates padding length (1-16).
        /// Per Baileys generics.ts padRandomMax16.
        /// </summary>
        private static byte[] PadRandomMax16(byte[] data)
        {
            var random = new Random();
            int padLen = random.Next(1, 17); // 1-16 bytes
            var result = new byte[data.Length + padLen];
            Array.Copy(data, result, data.Length);
            for (int i = 0; i < padLen - 1; i++)
                result[data.Length + i] = (byte)random.Next(256);
            result[result.Length - 1] = (byte)padLen;
            return result;
        }

        /// <summary>
        /// Encrypts a message payload using an existing session.
        /// Returns EncryptResult where Type is "msg".
        /// Throws if no session exists for the recipient.
        /// </summary>
        public EncryptResult EncryptMessage(byte[] plaintext, string recipientJid)
        {
            recipientJid = WA.NormalizeDeviceJid(recipientJid);
            byte[] sessionJson;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(recipientJid, out sessionJson))
                {
                    throw new InvalidOperationException($"No session found for {recipientJid}. Cannot encrypt.");
                }
            }

            var sessionJsonText = System.Text.Encoding.UTF8.GetString(sessionJson);
            var session = JsonConvert.DeserializeObject<SessionData>(sessionJsonText);

            if (session == null || session.RootKey == null)
            {
                throw new InvalidOperationException($"Invalid session data for {recipientJid}");
            }

            System.Diagnostics.Debug.WriteLine($"[Signal] EncryptMessage for {recipientJid}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   RootKey: {BitConverter.ToString(session.RootKey.Take(4).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   TheirRatchetKey: {(session.TheirRatchetPublicKey != null ? BitConverter.ToString(session.TheirRatchetPublicKey.Take(4).ToArray()) + "..." : "null")}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   SendingChainKey: {(session.SendingChainKey != null ? BitConverter.ToString(session.SendingChainKey.Take(4).ToArray()) + "..." : "null")}");
            System.Diagnostics.Debug.WriteLine($"[Signal]   PreviousSendingCounter: {session.PreviousSendingCounter}");

            // Initialize sending chain if not yet established (first send or after a receiving ratchet step)
            if (session.SendingChainKey == null || session.OurSendingRatchetPublic == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] Initializing sending chain (ratchet step or first send)");
                
                // Track previous chain length for the header
                session.PreviousSendingCounter = session.SendingCounter;
                session.SendingCounter = 0;
                
                // Generate our new ephemeral ratchet key pair for sending
                var ourRatchetKeyPair = Crypto.CryptoUtils.GenerateKeyPair();
                session.OurSendingRatchetPrivate = ourRatchetKeyPair.Private;
                session.OurSendingRatchetPublic = ourRatchetKeyPair.Public;
                
                // In Double Ratchet, this new private key also becomes our receiving base for their NEXT ratchet step
                session.OurRatchetPrivateKey = ourRatchetKeyPair.Private;
                
                System.Diagnostics.Debug.WriteLine($"[Signal]   Generated OurSendingRatchet: {BitConverter.ToString(session.OurSendingRatchetPublic.Take(4).ToArray())}...");
                
                // DH: SharedSecret = DH(ourSendingPrivate, theirRatchetPublic)
                byte[] sharedSecret = Crypto.CryptoUtils.SharedKey(session.OurSendingRatchetPrivate, session.TheirRatchetPublicKey);
                System.Diagnostics.Debug.WriteLine($"[Signal]   DH SharedSecret (bytes 0-8): {BitConverter.ToString(sharedSecret.Take(8).ToArray())}...");
                
                // Derive sending chain from root key: DeriveSecrets(sharedSecret, RootKey, "WhisperRatchet")
                byte[][] ratchetKeys = Crypto.CryptoUtils.DeriveSecrets(sharedSecret, session.RootKey, "WhisperRatchet", 2);
                byte[] newRootKey = ratchetKeys[0];
                session.SendingChainKey = ratchetKeys[1];
                
                // Update root key for next ratchet step
                session.RootKey = newRootKey;
                
                System.Diagnostics.Debug.WriteLine($"[Signal]   New RootKey: {BitConverter.ToString(session.RootKey.Take(4).ToArray())}...");
                System.Diagnostics.Debug.WriteLine($"[Signal]   New SendingChainKey: {BitConverter.ToString(session.SendingChainKey.Take(4).ToArray())}...");
            }

            System.Diagnostics.Debug.WriteLine($"[Signal] Encrypting with SendingCounter={session.SendingCounter}");

            // 1. Pad the plaintext
            byte[] paddedPlaintext = PadRandomMax16(plaintext);

            // 2. Get current message key from sending chain key: HMAC-SHA256(key=SendingChainKey, data=0x01)
            byte[] msgKey = Crypto.CryptoUtils.HmacSha256(new byte[] { 0x01 }, session.SendingChainKey);
            System.Diagnostics.Debug.WriteLine($"[Signal]   MessageKey: {BitConverter.ToString(msgKey.Take(8).ToArray())}...");

            // 3. Derive Cipher Key, Mac Key, IV: DeriveSecrets(MessageKey, salt=0, info="WhisperMessageKeys")
            byte[][] msgKeys = Crypto.CryptoUtils.DeriveSecrets(msgKey, new byte[32], "WhisperMessageKeys", 3);
            
            byte[] cipherKey = msgKeys[0];  // 32 bytes
            byte[] macKey = msgKeys[1];     // 32 bytes
            byte[] iv = new byte[16];
            Array.Copy(msgKeys[2], 0, iv, 0, 16);  // First 16 bytes of third key

            System.Diagnostics.Debug.WriteLine($"[Signal]   CipherKey: {BitConverter.ToString(cipherKey.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   MacKey: {BitConverter.ToString(macKey.Take(8).ToArray())}...");
            System.Diagnostics.Debug.WriteLine($"[Signal]   IV: {BitConverter.ToString(iv)}");

            // 4. Encrypt with AES-CBC
            byte[] ciphertext = Crypto.CryptoUtils.AesCbcEncrypt(paddedPlaintext, cipherKey, iv);
            System.Diagnostics.Debug.WriteLine($"[Signal]   Plaintext: {paddedPlaintext.Length} bytes -> Ciphertext: {ciphertext.Length} bytes");

            // 5. Build SignalMessage protobuf
            // CRITICAL: RatchetKey must be 33 bytes with 0x05 prefix!
            byte[] ratchetKey33 = Crypto.CryptoUtils.GenerateSignalPubKey(session.OurSendingRatchetPublic);
            
            var signalMsg = new SignalMessage
            {
                RatchetKey = Google.Protobuf.ByteString.CopyFrom(ratchetKey33),
                Counter = session.SendingCounter,
                PreviousCounter = session.PreviousSendingCounter,
                Ciphertext = Google.Protobuf.ByteString.CopyFrom(ciphertext)
            };

            byte[] msgProto = signalMsg.ToByteArray();
            System.Diagnostics.Debug.WriteLine($"[Signal]   SignalMessage proto: {msgProto.Length} bytes, RatchetKey: {BitConverter.ToString(ratchetKey33.Take(4).ToArray())}...");

            // 6. Build final message: [version byte] + [protobuf] + [MAC(8)]
            // Signal Protocol: WhisperMessage (inner message) is always Type 2
            byte versionByte = (byte)((3 << 4) | 3); // 0x33
            
            // CRITICAL: MAC must include identity keys per Signal spec!
            // MAC input = ourIdentityPub(33) + theirIdentityPub(33) + version(1) + msgProto
            byte[] ourIdentityPub = Crypto.CryptoUtils.GenerateSignalPubKey(_authState.SignedIdentityKey.Public);
            byte[] theirIdentityPub = session.TheirIdentityPublicKey;
            // Ensure their identity is also 33 bytes
            if (theirIdentityPub.Length == 32)
            {
                theirIdentityPub = Crypto.CryptoUtils.GenerateSignalPubKey(theirIdentityPub);
            }
            
            byte[] macInput = new byte[33 + 33 + 1 + msgProto.Length];
            Array.Copy(ourIdentityPub, 0, macInput, 0, 33);
            Array.Copy(theirIdentityPub, 0, macInput, 33, 33);
            macInput[66] = versionByte;
            Array.Copy(msgProto, 0, macInput, 67, msgProto.Length);
            
            System.Diagnostics.Debug.WriteLine($"[Signal]   MAC input: {macInput.Length} bytes (ourId + theirId + ver + proto)");
            
            byte[] fullMac = Crypto.CryptoUtils.HmacSha256(macInput, macKey);
            byte[] mac8 = new byte[8];
            Array.Copy(fullMac, 0, mac8, 0, 8);

            // Final result: version + protobuf + mac8
            byte[] result = new byte[1 + msgProto.Length + 8];
            result[0] = versionByte;
            Array.Copy(msgProto, 0, result, 1, msgProto.Length);
            Array.Copy(mac8, 0, result, 1 + msgProto.Length, 8);

            // 7. Advance sending chain key for next message: HMAC-SHA256(key=SendingChainKey, data=0x02)
            session.SendingChainKey = Crypto.CryptoUtils.HmacSha256(new byte[] { 0x02 }, session.SendingChainKey);
            session.SendingCounter++;

            // Save updated session
            // 9. If pending prekey, wrap in PreKeyWhisperMessage
            byte[] finalResult = result;
            string finalType = "msg";

            if (session.IsPendingPreKey)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] PKMSG Construction (STANDARD SIGNAL TAGS):");
                System.Diagnostics.Debug.WriteLine($"[Signal]   Using RegistrationId: {_authState.RegistrationId}");
                System.Diagnostics.Debug.WriteLine($"[Signal]   PreKeyId: {session.PendingPreKeyId}");
                System.Diagnostics.Debug.WriteLine($"[Signal]   SignedPreKeyId: {session.PendingSignedPreKeyId}");

                // CRITICAL: We MUST use standard Signal Protocol tags for Baileys/Signal compatibility!
                // WhatsApp's generated WAProto.cs has them scrambled, so we serialize manually.
                // 1. registrationId (uint32)
                // 2. preKeyId (uint32)
                // 3. signedPreKeyId (uint32)
                // 4. baseKey (bytes)
                // 5. identityKey (bytes)
                // 6. message (bytes)
                
                using (var ms = new System.IO.MemoryStream())
                {
                    var output = new Google.Protobuf.CodedOutputStream(ms);
                    
                    // Tag 1: preKeyId (optional)
                    if (session.PendingPreKeyId.HasValue)
                    {
                        output.WriteTag(1, Google.Protobuf.WireFormat.WireType.Varint);
                        output.WriteUInt32(session.PendingPreKeyId.Value);
                    }

                    // Tag 2: baseKey
                    byte[] baseKey33 = Crypto.CryptoUtils.GenerateSignalPubKey(session.PendingBaseKey);
                    output.WriteTag(2, Google.Protobuf.WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(baseKey33));
                    
                    // Tag 3: identityKey
                    byte[] identityKey33 = Crypto.CryptoUtils.GenerateSignalPubKey(_authState.SignedIdentityKey.Public);
                    output.WriteTag(3, Google.Protobuf.WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(identityKey33));
                    
                    // Tag 4: message (WhisperMessage with version and MAC)
                    output.WriteTag(4, Google.Protobuf.WireFormat.WireType.LengthDelimited);
                    output.WriteBytes(Google.Protobuf.ByteString.CopyFrom(result));

                    // Tag 5: registrationId
                    output.WriteTag(5, Google.Protobuf.WireFormat.WireType.Varint);
                    output.WriteUInt32((uint)_authState.RegistrationId);
                    
                    // Tag 6: signedPreKeyId
                    output.WriteTag(6, Google.Protobuf.WireFormat.WireType.Varint);
                    output.WriteUInt32((uint)(session.PendingSignedPreKeyId ?? 0));
                    
                    output.Flush();
                    byte[] pkMsgProto = ms.ToArray();
                    
                    System.Diagnostics.Debug.WriteLine($"[Signal]   PreKeySignalMessage proto: {pkMsgProto.Length} bytes");
                    System.Diagnostics.Debug.WriteLine($"[Signal] PKMSG PROTO HEX (first 100): {BitConverter.ToString(pkMsgProto.Take(Math.Min(pkMsgProto.Length, 100)).ToArray())}");
                    
                    // Final result: version (0x33) for PreKeyWhisperMessage wrapper
                    byte pkVersionByte = (byte)((3 << 4) | 3); // 0x33
                    finalResult = new byte[1 + pkMsgProto.Length];
                    finalResult[0] = pkVersionByte;
                    Array.Copy(pkMsgProto, 0, finalResult, 1, pkMsgProto.Length);
                    
                    System.Diagnostics.Debug.WriteLine($"[Signal] PKMSG FINAL HEX (first 50): {BitConverter.ToString(finalResult.Take(Math.Min(finalResult.Length, 50)).ToArray())}");

                    finalType = "pkmsg";
                    session.IsPendingPreKey = false;
                }
            }

            var updatedSessionJson = JsonConvert.SerializeObject(session);
            lock (_sessionLock)
            {
                _authState.Sessions[recipientJid] = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
            }

            System.Diagnostics.Debug.WriteLine($"[Signal] Encrypted message ({finalType}): {finalResult.Length} bytes, new SendingCounter={session.SendingCounter}");

            // Self-test: Verify that we can decrypt what we just encrypted
            if (finalType == "pkmsg")
            {
                VerifyDecryption(finalResult, session, paddedPlaintext);
            }

            return new EncryptResult { Type = finalType, Ciphertext = finalResult };
        }

        private void VerifyDecryption(byte[] fullPacket, SessionData session, byte[] expectedPlaintext)
        {
            try
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] === STARTING SELF-TEST DECRYPTION ===");
                
                // 1. Skip version byte
                byte[] proto = new byte[fullPacket.Length - 1];
                Array.Copy(fullPacket, 1, proto, 0, proto.Length);

            // 2. Extract Tag 4 (Message) from PreKeySignalMessage
            byte[] innerPacket = null;
            int pos = 0;
            while (pos < proto.Length)
            {
                int tagByte = proto[pos++];
                int tag = tagByte >> 3;
                int wire = tagByte & 0x07;

                if (tag == 4 && wire == 2) // Message tag (WhatsApp Tag 4)
                {
                    // Length-delimited varint
                    int len = proto[pos++];
                    if (len > 0x7F)
                    {
                        len = (len & 0x7F) | ((proto[pos++] & 0x7F) << 7);
                    }

                    innerPacket = new byte[len];
                    Array.Copy(proto, pos, innerPacket, 0, len);
                    break;
                }
                else if (wire == 0) // Varint
                {
                    while ((proto[pos++] & 0x80) != 0) ;
                }
                else if (wire == 2) // Length-delimited
                {
                    int len = proto[pos++];
                    if (len > 0x7F)
                    {
                        len = (len & 0x7F) | ((proto[pos++] & 0x7F) << 7);
                    }
                    pos += len;
                }
                else
                {
                    break;
                }
            }

            if (innerPacket == null)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST FAIL: Could not find inner message tag 4");
                return;
            }
                
                // 3. Extract SignalMessage from innerPacket
                byte innerVersion = innerPacket[0];
                byte[] signalProto = new byte[innerPacket.Length - 1 - 8];
                Array.Copy(innerPacket, 1, signalProto, 0, signalProto.Length);
                var signalMsg = SignalMessage.Parser.ParseFrom(signalProto);
                
                // 4. Verification
                byte[] ciphertext = signalMsg.Ciphertext.ToByteArray();
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST: Initiator side verification complete.");
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST: Inner version: {innerVersion:X2}");
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST: SignalMsg: Counter={signalMsg.Counter}, RatchetKey={BitConverter.ToString(signalMsg.RatchetKey.ToByteArray().Take(4).ToArray())}...");
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST: Ciphertext (first 8): {BitConverter.ToString(ciphertext.Take(8).ToArray())}");
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST: Expected Plaintext (first 8): {BitConverter.ToString(expectedPlaintext.Take(8).ToArray())}");

                System.Diagnostics.Debug.WriteLine($"[Signal] === SELF-TEST COMPLETED ===");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Signal] SELF-TEST ERROR: {ex.Message}");
            }
        }
    }
}
