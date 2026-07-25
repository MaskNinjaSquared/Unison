using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Unison.UWPApp.Crypto;
using Google.Protobuf;
using Proto;
using Newtonsoft.Json;
using Unison.UWPApp.Protocol;
using Unison.UWPApp.Services;

namespace Unison.UWPApp.Client
{
    /// <summary>
    /// Handles Signal V3 protocol decryption for WhatsApp messages.
    /// Ported/Simplified from Baileys and LibSignal.
    /// </summary>
    public class SignalHandler
    {
        /// <summary>
        /// Set to true to enable verbose Signal crypto logging (X3DH, DH, ChainKey, hex dumps).
        /// WARNING: This logs cryptographic secrets. Only enable for debugging.
        /// </summary>
        public static bool VerboseSignalLogging = false;
        private readonly AuthState _authState;
        private readonly IKeyStore _keyStore;
        private readonly object _sessionLock = new object();
        private readonly object _senderKeyPersistQueueLock = new object();
        private readonly Dictionary<string, Task> _senderKeyPersistTails = new Dictionary<string, Task>(StringComparer.Ordinal);

        /// <summary>
        /// Normalizes a Curve25519 public key to 32-byte raw format (strips Signal 0x05 prefix when present).
        /// Keeping session ratchet keys in one canonical format avoids false ratchet-advance mismatches.
        /// </summary>
        private static byte[] NormalizeCurve25519PubKey(byte[] key)
        {
            if (key == null) return null;
            if (key.Length == 33 && key[0] == 0x05)
            {
                var trimmed = new byte[32];
                Array.Copy(key, 1, trimmed, 0, 32);
                return trimmed;
            }
            return key;
        }

        private static string Fingerprint(byte[] data, int bytes = 6)
        {
            if (data == null || data.Length == 0) return "null";
            int take = Math.Min(bytes, data.Length);
            return BitConverter.ToString(data.Take(take).ToArray());
        }


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
            if (string.IsNullOrWhiteSpace(jid)) return;

            jid = WA.NormalizeDeviceJid(jid);
            byte[] sessionData;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(jid, out sessionData))
                    return;

                sessionData = (byte[])sessionData.Clone();
            }

            if (_keyStore != null)
            {
                await _keyStore.SetSessionAsync(jid, sessionData);
            }
        }

        public async System.Threading.Tasks.Task CloneSessionAliasAsync(string sourceJid, string aliasJid, bool overwrite = false)
        {
            if (string.IsNullOrWhiteSpace(sourceJid) || string.IsNullOrWhiteSpace(aliasJid))
            {
                return;
            }

            sourceJid = WA.NormalizeDeviceJid(sourceJid);
            aliasJid = WA.NormalizeDeviceJid(aliasJid);
            if (string.Equals(sourceJid, aliasJid, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            byte[] sessionData;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(sourceJid, out sessionData) || sessionData == null)
                {
                    return;
                }

                if (!overwrite && _authState.Sessions.ContainsKey(aliasJid))
                {
                    return;
                }

                _authState.Sessions[aliasJid] = (byte[])sessionData.Clone();
                sessionData = (byte[])sessionData.Clone();
            }

            if (_keyStore != null)
            {
                await _keyStore.SetSessionAsync(aliasJid, sessionData);
            }

            WhatsAppService.Log($"[Signal] Cloned session alias from {sourceJid} to {aliasJid}");
        }

        public System.Threading.Tasks.Task MirrorOwnPnLidSessionAliasesAsync(string reason)
        {
            WhatsAppService.Log($"[Signal] Own PN/LID session alias mirror skipped ({reason}); sessions are address-specific");
            return System.Threading.Tasks.Task.CompletedTask;
        }

        private async System.Threading.Tasks.Task PersistOwnPnLidAliasForSessionAsync(string sourceJid, byte[] sessionData, bool overwrite, string reason)
        {
            string aliasJid = GetOwnPnLidAliasForJid(sourceJid);
            if (string.IsNullOrWhiteSpace(aliasJid) ||
                string.Equals(sourceJid, aliasJid, StringComparison.OrdinalIgnoreCase) ||
                sessionData == null)
            {
                return;
            }

            bool shouldPersist = false;
            lock (_sessionLock)
            {
                if (overwrite || !_authState.Sessions.ContainsKey(aliasJid))
                {
                    _authState.Sessions[aliasJid] = (byte[])sessionData.Clone();
                    shouldPersist = true;
                }
            }

            if (!shouldPersist)
            {
                return;
            }

            if (_keyStore != null)
            {
                await _keyStore.SetSessionAsync(aliasJid, sessionData);
            }

            WhatsAppService.Log($"[Signal] Mirrored own PN/LID session alias {sourceJid} -> {aliasJid} ({reason})");
        }

        private sealed class SessionAliasPair
        {
            public string Source { get; set; }
            public string Alias { get; set; }
        }

        private List<SessionAliasPair> BuildOwnPnLidAliasPairs()
        {
            var pairs = new List<SessionAliasPair>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddPair(string source, string alias)
            {
                source = WA.NormalizeDeviceJid(source);
                alias = WA.NormalizeDeviceJid(alias);
                if (string.IsNullOrWhiteSpace(source) ||
                    string.IsNullOrWhiteSpace(alias) ||
                    string.Equals(source, alias, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string key = source + "\n" + alias;
                if (seen.Add(key))
                {
                    pairs.Add(new SessionAliasPair { Source = source, Alias = alias });
                }
            }

            string ownPn = WA.NormalizeDeviceJid(_authState.Me?.Id);
            string ownLid = WA.NormalizeDeviceJid(_authState.Me?.Lid);
            if (string.IsNullOrWhiteSpace(ownPn) || string.IsNullOrWhiteSpace(ownLid))
            {
                return pairs;
            }

            AddPair(ownPn, GetOwnPnLidAliasForJid(ownPn));
            AddPair(WA.GetBaseJid(ownPn), WA.GetBaseJid(ownLid));

            WA.JidDecode(ownPn, out var pnUser, out var pnServer, out var pnDevice);
            WA.JidDecode(ownLid, out var lidUser, out var lidServer, out var lidDevice);
            if (!string.IsNullOrWhiteSpace(pnUser) && !string.IsNullOrWhiteSpace(pnServer) &&
                !string.IsNullOrWhiteSpace(lidUser) && !string.IsNullOrWhiteSpace(lidServer))
            {
                if (pnDevice > 0)
                {
                    AddPair(BuildJid(pnUser, pnServer, pnDevice), BuildJid(lidUser, lidServer, pnDevice));
                }

                if (lidDevice > 0)
                {
                    AddPair(BuildJid(pnUser, pnServer, lidDevice), BuildJid(lidUser, lidServer, lidDevice));
                }
            }

            return pairs;
        }

        private string GetOwnPnLidAliasForJid(string jid)
        {
            jid = WA.NormalizeDeviceJid(jid);
            string ownPn = WA.NormalizeDeviceJid(_authState.Me?.Id);
            string ownLid = WA.NormalizeDeviceJid(_authState.Me?.Lid);
            if (string.IsNullOrWhiteSpace(jid) ||
                string.IsNullOrWhiteSpace(ownPn) ||
                string.IsNullOrWhiteSpace(ownLid))
            {
                return null;
            }

            WA.JidDecode(jid, out var jidUser, out var jidServer, out var jidDevice);
            WA.JidDecode(ownPn, out var ownPnUser, out var ownPnServer, out _);
            WA.JidDecode(ownLid, out var ownLidUser, out var ownLidServer, out _);

            if (string.IsNullOrWhiteSpace(jidUser) || string.IsNullOrWhiteSpace(jidServer))
            {
                return null;
            }

            if (string.Equals(jidUser, ownPnUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(jidServer, ownPnServer, StringComparison.OrdinalIgnoreCase))
            {
                return BuildJid(ownLidUser, ownLidServer, jidDevice);
            }

            if (string.Equals(jidUser, ownLidUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(jidServer, ownLidServer, StringComparison.OrdinalIgnoreCase))
            {
                return BuildJid(ownPnUser, ownPnServer, jidDevice);
            }

            return null;
        }

        private static string BuildJid(string user, string server, int device)
        {
            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(server))
            {
                return null;
            }

            return WA.NormalizeDeviceJid(device > 0 ? $"{user}:{device}@{server}" : $"{user}@{server}");
        }

        /// <summary>
        /// Removes a session from memory and persistent storage.
        /// Used to force fresh pkmsg/session re-establishment after retry receipts.
        /// </summary>
        public async System.Threading.Tasks.Task ResetSessionAsync(string jid)
        {
            if (string.IsNullOrWhiteSpace(jid))
            {
                return;
            }

            jid = WA.NormalizeDeviceJid(jid);
            bool removed = false;
            lock (_sessionLock)
            {
                removed = _authState.Sessions.Remove(jid);
            }

            if (removed)
            {
                WhatsAppService.Log($"[Signal] Reset session in memory for {jid}");
            }

            if (_keyStore != null)
            {
                try
                {
                    await _keyStore.RemoveSessionAsync(jid);
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[Signal] Failed to remove persisted session for {jid}: {ex.Message}");
                }
            }
        }

        public async System.Threading.Tasks.Task ResetSessionsForSenderAsync(string senderJid)
        {
            var candidates = BuildSessionCandidatesForSender(senderJid);
            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(senderJid))
            {
                candidates.Add(WA.NormalizeDeviceJid(senderJid));
            }

            foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                await ResetSessionAsync(candidate);
            }

            WhatsAppService.Log($"[Signal] Reset {candidates.Count} Signal session candidate(s) for sender {senderJid}");
        }

        /// <summary>
        /// Loads all sessions from persistent storage into memory
        /// </summary>
        public async System.Threading.Tasks.Task LoadSessionsFromStoreAsync()
        {
            if (_keyStore == null) return;

            var jids = await _keyStore.GetAllSessionJidsAsync();
            int loadedSessions = 0;
            foreach (var jid in jids)
            {
                var data = await _keyStore.GetSessionAsync(jid);
                if (data != null)
                {
                    lock (_sessionLock)
                    {
                        _authState.Sessions[jid] = data;
                    }
                    loadedSessions++;
                }
            }

            var senderKeys = await _keyStore.GetAllSenderKeysAsync();
            int loadedSenderKeys = 0;
            foreach (var kv in senderKeys)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                {
                    continue;
                }

                lock (_sessionLock)
                {
                    _authState.Sessions[kv.Key] = kv.Value;
                }
                loadedSenderKeys++;
            }

            WhatsAppService.Log($"[Signal] Loaded {loadedSessions} sessions and {loadedSenderKeys} sender keys from store (memory entries={_authState.Sessions.Count})");
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
            public const int MaxSkippedMessageKeys = 2000;
            public const int MaxPreviousSessions = 40;

            // Receiving chain (established when we received their pkmsg)
            public byte[] RootKey { get; set; }
            public byte[] ChainKey { get; set; }  // Receiving chain key
            public uint Counter { get; set; }      // Receiving counter
            public byte[] TheirIdentityPublicKey { get; set; }
            public byte[] OurRatchetPrivateKey { get; set; }  // Our key used for receiving
            public byte[] TheirRatchetPublicKey { get; set; } // Their current public key
            public List<SkippedMessageKeyState> SkippedMessageKeys { get; set; }
            
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

            // Baileys/libsignal SessionRecord keeps old closed sessions by base key.
            // Keep a bounded ring so delayed peer/offline messages encrypted to a prior
            // session can still decrypt after a new prekey session is opened.
            public List<SessionData> PreviousSessions { get; set; }
        }

        public class SkippedMessageKeyState
        {
            public byte[] RatchetKey { get; set; }
            public uint Counter { get; set; }
            public byte[] MessageKey { get; set; }
        }

        /// <summary>
        /// Stores SenderKey state for group message decryption.
        /// </summary>
        private class SenderKeyState
        {
            public const int MaxSenderMessageKeys = 2000;
            public int KeyId { get; set; }
            public int Iteration { get; set; }
            public byte[] ChainKey { get; set; }    // 32 bytes - seed for HMAC chain
            public byte[] SigningKey { get; set; }   // 33 bytes (0x05 + 32) - Ed25519 public key
            public byte[] SigningPrivateKey { get; set; } // 32 bytes - Curve25519 private key used for XEdDSA signing
            public List<SenderMessageKeyState> MessageKeys { get; set; }
        }

        private class SenderMessageKeyState
        {
            public int Iteration { get; set; }
            public byte[] Seed { get; set; }
        }

        public class GroupEncryptResult
        {
            public byte[] Ciphertext { get; set; }
            public byte[] SenderKeyDistributionMessage { get; set; }
            public int KeyId { get; set; }
            public int Iteration { get; set; }
            public bool CreatedNewSenderKey { get; set; }
        }

        /// <summary>
        /// Processes a SenderKeyDistributionMessage to store the sender's group key.
        /// Per Baileys decode-wa-message.ts:305-315 and GroupSessionBuilder.process().
        /// </summary>
        public void ProcessSenderKeyDistribution(string senderJid, 
            Proto.Message.Types.SenderKeyDistributionMessage dist)
        {
            if (dist == null || dist.AxolotlSenderKeyDistributionMessage == null || dist.AxolotlSenderKeyDistributionMessage.IsEmpty)
            {
                WhatsAppService.Log("[Signal] SenderKeyDistribution: missing axolotl data, skipping");
                return;
            }

            string groupId = dist.GroupId;
            if (string.IsNullOrEmpty(groupId))
            {
                WhatsAppService.Log("[Signal] SenderKeyDistribution: missing groupId, skipping");
                return;
            }

            try
            {
                // Per Baileys sender-key-distribution-message.ts:
                // axolotlSenderKeyDistributionMessage = [version byte] + [protobuf SenderKeyDistributionMessage]
                var raw = dist.AxolotlSenderKeyDistributionMessage.ToByteArray();
                if (raw.Length < 2)
                {
                    WhatsAppService.Log("[Signal] SenderKeyDistribution: data too short");
                    return;
                }

                // Skip version byte (first byte), parse the rest as SenderKeyDistributionMessage protobuf
                var skDist = Proto.SenderKeyDistributionMessage.Parser.ParseFrom(raw, 1, raw.Length - 1);

                var state = new SenderKeyState
                {
                    KeyId = (int)skDist.Id,
                    Iteration = (int)skDist.Iteration,
                    ChainKey = skDist.ChainKey.ToByteArray(),
                    SigningKey = skDist.SigningKey.ToByteArray(),
                    MessageKeys = new List<SenderMessageKeyState>()
                };

                // Key format: sk:{groupId}:{normalizedSenderJid}
                string normalizedSender = WA.NormalizeDeviceJid(senderJid);
                string key = $"sk:{groupId}:{normalizedSender}";

                WhatsAppService.Log($"[Signal] Stored SenderKey for {key}: keyId={state.KeyId}, iteration={state.Iteration}, chainKey={state.ChainKey.Length}B, signingKey={state.SigningKey.Length}B");
                PersistSenderKeyState(groupId, normalizedSender, NormalizeSenderKeyState(state), "sender key distribution");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Signal] Failed to process SenderKeyDistribution: {ex.Message}");
            }
        }

        public GroupEncryptResult EncryptGroupMessage(string groupJid, string senderJid, byte[] plaintext)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
                throw new ArgumentNullException(nameof(groupJid));
            if (string.IsNullOrWhiteSpace(senderJid))
                throw new ArgumentNullException(nameof(senderJid));
            if (plaintext == null)
                throw new ArgumentNullException(nameof(plaintext));

            string normalizedSender = WA.NormalizeDeviceJid(senderJid);
            bool createdNewSenderKey;
            var state = LoadOrCreateLocalSenderKeyState(groupJid, normalizedSender, out createdNewSenderKey);

            var distributionMessage = BuildSenderKeyDistributionMessageBytes(state);
            int iteration = state.Iteration;
            byte[] currentChainKey = state.ChainKey;

            byte[] messageKeySeed = DeriveSenderMessageKeySeed(currentChainKey);
            byte[] iv;
            byte[] cipherKey;
            DeriveSenderMessageCipherMaterial(messageKeySeed, out iv, out cipherKey);

            byte[] ciphertext = CryptoUtils.AesCbcEncrypt(plaintext, cipherKey, iv);
            byte[] serialized = BuildSenderKeyMessageBytes(state.KeyId, iteration, ciphertext, state.SigningPrivateKey);

            state.Iteration = iteration + 1;
            state.ChainKey = AdvanceSenderChainKey(currentChainKey);
            PersistSenderKeyState(groupJid, normalizedSender, state, "outgoing group send");

            return new GroupEncryptResult
            {
                Ciphertext = serialized,
                SenderKeyDistributionMessage = distributionMessage,
                KeyId = state.KeyId,
                Iteration = iteration,
                CreatedNewSenderKey = createdNewSenderKey
            };
        }

        public byte[] GetSenderKeyDistributionMessage(string groupJid, string senderJid)
        {
            if (string.IsNullOrWhiteSpace(groupJid))
                throw new ArgumentNullException(nameof(groupJid));
            if (string.IsNullOrWhiteSpace(senderJid))
                throw new ArgumentNullException(nameof(senderJid));

            string normalizedSender = WA.NormalizeDeviceJid(senderJid);
            var state = LoadOrCreateLocalSenderKeyState(groupJid, normalizedSender);
            return BuildSenderKeyDistributionMessageBytes(state);
        }

        public bool TryGetSenderKeyDistributionMessage(string groupJid, string senderJid, out byte[] distributionMessage)
        {
            distributionMessage = null;
            if (string.IsNullOrWhiteSpace(groupJid) || string.IsNullOrWhiteSpace(senderJid))
            {
                return false;
            }

            string normalizedSender = WA.NormalizeDeviceJid(senderJid);
            var state = LoadExistingLocalSenderKeyState(groupJid, normalizedSender);
            if (state == null)
            {
                return false;
            }

            distributionMessage = BuildSenderKeyDistributionMessageBytes(state);
            return distributionMessage != null && distributionMessage.Length > 0;
        }

        public void InitializeOutgoingSession(string jid, SocketClient.PreKeyBundle bundle)
        {
            jid = WA.NormalizeDeviceJid(jid);
            WhatsAppService.Log($"[Signal] InitializeOutgoingSession for {jid}");
            if (VerboseSignalLogging)
            {
                WhatsAppService.Log($"[Signal]   Our RegistrationId: {_authState.RegistrationId}");
                WhatsAppService.Log($"[Signal]   Our IdentityKey.Public: {BitConverter.ToString(_authState.SignedIdentityKey.Public.Take(8).ToArray())}...");
                WhatsAppService.Log($"[Signal]   Bundle RegistrationId: {bundle.RegistrationId}, SignedPreKeyId: {bundle.SignedPreKeyId}, OneTimePreKeyId: {bundle.OneTimePreKeyId}");
            }
            
            // 1. Generate our ephemeral key pair (EK_A)
            var ourEphemeral = CryptoUtils.GenerateKeyPair();
            
            // 2. DH Exchanges (X3DH)
            
            // DH1 = DH(IK_A_private, SPK_B)
            byte[] dh1 = CryptoUtils.SharedKey(_authState.SignedIdentityKey.Private, bundle.SignedPreKey);
            
            // DH2 = DH(EK_A_private, IK_B)
            byte[] dh2 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.IdentityKey);
            
            // DH3 = DH(EK_A_private, SPK_B)
            byte[] dh3 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.SignedPreKey);
            
            byte[] sharedSecret;
            if (bundle.OneTimePreKey != null)
            {
                // DH4 = DH(EK_A_private, OPK_B)
                byte[] dh4 = CryptoUtils.SharedKey(ourEphemeral.Private, bundle.OneTimePreKey);

                
                sharedSecret = new byte[32 * 5];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF; // Mandatory 0xFF prefix
                Buffer.BlockCopy(dh1, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(dh2, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(dh3, 0, sharedSecret, 96, 32);
                Buffer.BlockCopy(dh4, 0, sharedSecret, 128, 32);

            }
            else
            {
                sharedSecret = new byte[32 * 4];
                for (int i = 0; i < 32; i++) sharedSecret[i] = 0xFF; // Mandatory 0xFF prefix
                Buffer.BlockCopy(dh1, 0, sharedSecret, 32, 32);
                Buffer.BlockCopy(dh2, 0, sharedSecret, 64, 32);
                Buffer.BlockCopy(dh3, 0, sharedSecret, 96, 32);
            }
            
            // 3. Derive Root Key and Initial Chain Key using DeriveSecrets (matching BaileysCSharp)
            byte[] salt = new byte[32]; // All zeros salt
            byte[][] masterKeys = CryptoUtils.DeriveSecrets(sharedSecret, salt, "WhisperText", 2);
            
            byte[] rootKey = masterKeys[0];
            byte[] initialChainKey = masterKeys[1];
            if (VerboseSignalLogging)
            {
                WhatsAppService.Log($"[Signal]   RootKey: {BitConverter.ToString(rootKey.Take(8).ToArray())}..., ChainKey: {BitConverter.ToString(initialChainKey.Take(8).ToArray())}...");
            }

            SessionData priorSession = null;
            lock (_sessionLock)
            {
                if (_authState.Sessions.TryGetValue(jid, out var existingBytes) && existingBytes != null)
                {
                    try
                    {
                        priorSession = NormalizeSessionData(JsonConvert.DeserializeObject<SessionData>(System.Text.Encoding.UTF8.GetString(existingBytes)));
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to parse existing session for {jid} during outgoing session init: {ex.Message}");
                    }
                }
            }
            
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
                TheirRatchetPublicKey = NormalizeCurve25519PubKey(bundle.SignedPreKey), // Start with their SignedPreKey as the ratchet key
                OurRatchetPrivateKey = ourEphemeral.Private, // Our base key for their next ratchet step
                SendingCounter = 0,
                PreviousSendingCounter = 0,
                IsPendingPreKey = true,
                PendingSignedPreKeyId = bundle.SignedPreKeyId,
                PendingPreKeyId = bundle.OneTimePreKeyId,
                PendingBaseKey = ourEphemeral.Public,
                SkippedMessageKeys = new List<SkippedMessageKeyState>(),
                CanSend = true,  // This session was established for sending
                PreviousSessions = BuildPreviousSessionRing(priorSession)
            };

            if (session.PreviousSessions.Count > 0)
            {
                WhatsAppService.Log($"[Signal] Archived {session.PreviousSessions.Count} previous session(s) while initializing outgoing session for {jid}");
            }
            
            var sessionJson = JsonConvert.SerializeObject(session);
            lock (_sessionLock)
            {
                _authState.Sessions[jid] = System.Text.Encoding.UTF8.GetBytes(sessionJson);
            }
            WhatsAppService.Log($"[Signal] New session initialized and saved for {jid}");
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
                    var session = NormalizeSessionData(JsonConvert.DeserializeObject<SessionData>(System.Text.Encoding.UTF8.GetString(sessionJson)));
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
        public byte[] DecryptMessage(byte[] data, string senderJid, string signalType = null, string groupJid = null)
        {
            senderJid = WA.NormalizeDeviceJid(senderJid);
            if (data == null || data.Length < 1) return null;

            byte version = (byte)(data[0] >> 4);
            byte type = (byte)(data[0] & 0x0F);
            
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Received packet: version={version}, headerTypeNibble={type}, signalType={signalType ?? "null"}, length={data.Length}");

            if (version > 3)
            {
                throw new Exception($"Unsupported Signal version: {version}");
            }

            // Signal messages have: version byte (1) + protobuf + [MAC (8)]
            // For pkmsg (Type 3), it's the same but the nested message usually has the MAC.
            
            byte[] serialized;
            byte[] alternateMsgSerialized = null;
            byte[] mac = null;

            // IMPORTANT: Choose decoding path from WA node enc/@type first.
            // Header low nibble is not a reliable discriminator for pkmsg vs msg in this implementation.
            if (signalType == "skmsg")
            {
                // skmsg has a 64-byte signature at the end
                if (data.Length < 65) return null;
                serialized = new byte[data.Length - 1 - 64];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] skmsg: Stripped 64-byte signature. Protobuf length: {serialized.Length}");
            }
            else if (signalType == "pkmsg")
            {
                // Top-level pkmsg has no MAC
                serialized = new byte[data.Length - 1];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] pkmsg: Sliced version, length={serialized.Length}");
            }
            else if (signalType == "msg")
            {
                if (data.Length < 10) return null;
                serialized = new byte[data.Length - 1 - 8];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                // Some WA message variants appear to carry msg payloads without the trailing MAC bytes in this layer.
                // Keep an alternate framing candidate to retry if decryption fails with the primary slice.
                alternateMsgSerialized = new byte[data.Length - 1];
                Array.Copy(data, 1, alternateMsgSerialized, 0, alternateMsgSerialized.Length);
                
                mac = new byte[8];
                Array.Copy(data, data.Length - 8, mac, 0, 8);
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] msg framing=primary(strip-mac): protobuf length={serialized.Length}, mac={BitConverter.ToString(mac)}");
            }
            else if (type == 3) // legacy fallback
            {
                serialized = new byte[data.Length - 1];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Fallback pkmsg by header nibble, length={serialized.Length}");
            }
            else // legacy fallback for msg
            {
                if (data.Length < 10) return null;
                serialized = new byte[data.Length - 1 - 8];
                Array.Copy(data, 1, serialized, 0, serialized.Length);
                mac = new byte[8];
                Array.Copy(data, data.Length - 8, mac, 0, 8);
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Fallback msg by header nibble, protobuf length={serialized.Length}");
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
                        WhatsAppService.Log($"[Signal] Failed to parse as SenderKeyMessage: {ex.Message}");
                    }
                }
                else if (signalType == "pkmsg" || (string.IsNullOrEmpty(signalType) && type == 3))
                {
                    try {
                        pkMsg = PreKeySignalMessage.Parser.ParseFrom(serialized);
                    } catch (Exception ex) {
                        WhatsAppService.Log($"[Signal] Failed to parse as PreKeySignalMessage: {ex.Message}");
                    }
                }

                if (skMsg != null)
                {
                    WhatsAppService.Log($"[Signal] Processing skmsg (SenderKey) from {senderJid}, id={skMsg.Id}");
                    return DecryptSenderKeyMessage(skMsg, senderJid, groupJid);
                }

                if (pkMsg != null && pkMsg.HasBaseKey)
                {
                    WhatsAppService.Log($"[Signal] Processing pkmsg from {senderJid}, preKeyId={pkMsg.PreKeyId}");
                    return EstablishSessionAndDecrypt(pkMsg, senderJid);
                }

                // Fallback or Type 2
                try {
                    signalMsg = SignalMessage.Parser.ParseFrom(serialized);
                } catch (Exception ex) {
                    WhatsAppService.Log($"[Signal] Failed to parse as SignalMessage: {ex.Message}");
                }

                if (signalMsg != null)
                {
                    WhatsAppService.Log($"[Signal] Processing msg from {senderJid}, counter={signalMsg.Counter}");
                    var primaryPlaintext = DecryptWithExistingSession(signalMsg, senderJid);
                    if (primaryPlaintext != null)
                    {
                        return primaryPlaintext;
                    }

                    // Retry with alternate framing for WA msg variants where the outer layer does not include MAC bytes.
                    if (signalType == "msg" && alternateMsgSerialized != null)
                    {
                        try
                        {
                            var altSignalMsg = SignalMessage.Parser.ParseFrom(alternateMsgSerialized);
                            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] msg framing=fallback(no-strip): counter={altSignalMsg.Counter}");
                            var altPlaintext = DecryptWithExistingSession(altSignalMsg, senderJid);
                            if (altPlaintext != null)
                            {
                                return altPlaintext;
                            }
                        }
                        catch (Exception ex)
                        {
                            WhatsAppService.Log($"[Signal] msg fallback parse failed: {ex.Message}");
                        }
                    }

                    return null;
                }

                return null; 
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Signal] Decryption failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Decrypts a SenderKey (group) message using the stored SenderKeyState.
        /// Per Baileys GroupCipher.decrypt(), sender-chain-key.ts, sender-message-key.ts.
        /// </summary>
        private byte[] DecryptSenderKeyMessage(SenderKeyMessage skMsg, string senderJid, string groupJid)
        {
            if (string.IsNullOrEmpty(groupJid))
            {
                WhatsAppService.Log($"[Signal] No groupJid for skmsg from {senderJid}, cannot decrypt");
                return null;
            }

            // Key format matches ProcessSenderKeyDistribution: sk:{groupId}:{senderJid}
            string key = $"sk:{groupJid}:{senderJid}";
            byte[] stateBytes;
            lock (_sessionLock)
            {
                if (!_authState.Sessions.TryGetValue(key, out stateBytes))
                {
                    stateBytes = null;
                }
            }

            if (stateBytes == null && _keyStore != null)
            {
                try
                {
                    stateBytes = _keyStore.GetSenderKeyAsync(groupJid, senderJid).GetAwaiter().GetResult();
                    if (stateBytes != null)
                    {
                        lock (_sessionLock)
                        {
                            _authState.Sessions[key] = stateBytes;
                        }
                        WhatsAppService.Log($"[Signal] Restored SenderKey from store for {key}");
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[Signal] Failed to restore SenderKey from store for {key}: {ex.Message}");
                }
            }

            if (stateBytes == null)
            {
                WhatsAppService.Log($"[Signal] No SenderKey found for {key}");
                return null;
            }

            try
            {
                // Deserialize stored state
                string json = System.Text.Encoding.UTF8.GetString(stateBytes);
                var state = NormalizeSenderKeyState(JsonConvert.DeserializeObject<SenderKeyState>(json));

                if (state == null || state.ChainKey == null)
                {
                    WhatsAppService.Log($"[Signal] Invalid SenderKeyState for {key}");
                    return null;
                }

                // Verify keyId matches
                if (state.KeyId != (int)skMsg.Id)
                {
                    WhatsAppService.Log($"[Signal] SenderKey keyId mismatch: stored={state.KeyId}, msg={skMsg.Id}");
                    return null;
                }

                int targetIteration = (int)skMsg.Iteration;

                // Advance chain key from stored iteration to target iteration
                // Per Baileys sender-chain-key.ts: getNext() = HMAC(chainKey, 0x02)
                byte[] currentChainKey = state.ChainKey;
                int currentIteration = state.Iteration;

                if (currentIteration > targetIteration)
                {
                    WhatsAppService.Log($"[Signal] SenderKey old-iteration replay path: current={currentIteration}, target={targetIteration}, cachedKeys={state.MessageKeys.Count}");
                    byte[] cachedSeed;
                    if (!TryRemoveSenderMessageKey(state, targetIteration, out cachedSeed))
                    {
                        WhatsAppService.Log($"[Signal] SenderKey cache miss for old iteration {targetIteration} (current {currentIteration})");
                        return null;
                    }

                    WhatsAppService.Log($"[Signal] SenderKey cache hit for old iteration {targetIteration} (current {currentIteration})");

                    byte[] cachedIv;
                    byte[] cachedCipherKey;
                    DeriveSenderMessageCipherMaterial(cachedSeed, out cachedIv, out cachedCipherKey);

                    byte[] cachedCiphertext = skMsg.Ciphertext.ToByteArray();
                    byte[] cachedPlaintext = CryptoUtils.AesCbcDecrypt(cachedCiphertext, cachedCipherKey, cachedIv);
                    PersistSenderKeyState(groupJid, senderJid, state, $"sender key old iteration cache hit {targetIteration}", waitForDisk: false);
                    WhatsAppService.Log($"[Signal] Successfully decrypted cached skmsg payload: {cachedPlaintext.Length} bytes");
                    return cachedPlaintext;
                }

                if (targetIteration - currentIteration > SenderKeyState.MaxSenderMessageKeys)
                {
                    WhatsAppService.Log($"[Signal] SenderKey: too many iterations ahead ({targetIteration - currentIteration})");
                    return null;
                }

                // Ratchet chain forward to target iteration
                int cachedSkippedKeys = 0;
                while (currentIteration < targetIteration)
                {
                    AddSenderMessageKey(state, currentIteration, DeriveSenderMessageKeySeed(currentChainKey));
                    cachedSkippedKeys++;
                    currentChainKey = AdvanceSenderChainKey(currentChainKey);
                    currentIteration++;
                }

                // Derive message key seed: HMAC(chainKey, 0x01)
                byte[] messageKeySeed = DeriveSenderMessageKeySeed(currentChainKey);

                // Derive iv + cipherKey from seed using HKDF
                // Per Baileys sender-message-key.ts:
                //   derivative = deriveSecrets(seed, Buffer.alloc(32), 'WhisperGroup')
                //   iv = derivative[0].slice(0, 16)
                //   cipherKey = derivative[0].slice(16) + derivative[1].slice(0, 16)  (= 32 bytes)
                byte[] iv;
                byte[] cipherKey;
                DeriveSenderMessageCipherMaterial(messageKeySeed, out iv, out cipherKey);

                // Decrypt ciphertext using AES-CBC
                byte[] ciphertext = skMsg.Ciphertext.ToByteArray();
                byte[] plaintext = CryptoUtils.AesCbcDecrypt(ciphertext, cipherKey, iv);

                // Update stored state: advance chain one more step for next message
                state.Iteration = targetIteration + 1;
                state.ChainKey = AdvanceSenderChainKey(currentChainKey);
                PersistSenderKeyState(groupJid, senderJid, state, $"sender key decrypt iteration {targetIteration}", waitForDisk: false);
                return plaintext;
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Signal] SenderKey decryption failed: {ex.Message}");
                return null;
            }
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
                    WhatsAppService.Log($"[Signal] Warning: One-time pre-key {pkMsg.PreKeyId} not found locally.");
                }
            }

            // 2. Perform DH calculations (X3DH)
            byte[] theirIdentityKey = pkMsg.IdentityKey.ToByteArray();
            byte[] theirBaseKey = pkMsg.BaseKey.ToByteArray();

            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] X3DH: TheirIdentity={BitConverter.ToString(theirIdentityKey.Take(4).ToArray())}..., TheirBase={BitConverter.ToString(theirBaseKey.Take(4).ToArray())}...");

            var a1 = CryptoUtils.SharedKey(ourIdentityKey.Private, theirBaseKey);
            var a2 = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirIdentityKey);
            var a3 = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirBaseKey);

            byte[] sharedSecret;
            if (ourOneTimePreKey != null)
            {
                var a4 = CryptoUtils.SharedKey(ourOneTimePreKey.Private, theirBaseKey);

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
            


            // 3. Derive Root Key
            byte[] salt = new byte[32]; 
            byte[] info = System.Text.Encoding.UTF8.GetBytes("WhisperText");
            byte[] masterKey = CryptoUtils.Hkdf(sharedSecret, 64, salt, info);
            
            byte[] rootKey = masterKey.Take(32).ToArray();
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] RootKey (initial): {BitConverter.ToString(rootKey.Take(4).ToArray())}...");

            // 4. Parse Nested Message to get correct Ratchet Key
            byte[] msgBytes = pkMsg.Message.ToByteArray();
            byte[] originalMac = null;
            if (msgBytes.Length > 0)
            {
                if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Nested header byte: {msgBytes[0]:X2}");
                if ((msgBytes[0] & 0xF0) == 0x30)
                {
                    if (msgBytes.Length >= 10)
                    {
                        originalMac = new byte[8];
                        Array.Copy(msgBytes, msgBytes.Length - 8, originalMac, 0, 8);
                        byte[] innerSerialized = new byte[msgBytes.Length - 1 - 8];
                        Array.Copy(msgBytes, 1, innerSerialized, 0, innerSerialized.Length);
                        msgBytes = innerSerialized;
                        if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Stripped nested header and 8-byte MAC, length={msgBytes.Length}");
                    }
                    else
                    {
                        // Fallback for extremely short packets (shouldn't happen for valid Signal messages)
                        byte[] stripped = new byte[msgBytes.Length - 1];
                        Array.Copy(msgBytes, 1, stripped, 0, stripped.Length);
                        msgBytes = stripped;
                        if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Stripped nested header ONLY (too short for MAC), length={msgBytes.Length}");
                    }
                }
            }

            SignalMessage signalMsg = SignalMessage.Parser.ParseFrom(msgBytes);
            byte[] theirRatchetKey = NormalizeCurve25519PubKey(signalMsg.RatchetKey.ToByteArray());

            // 5. Initial Ratchet Step (Receiver)
            // Use the ephemeral key FROM THE MESSAGE, which should match theirBaseKey in the first message.
            byte[] ratchetSharedSecret = CryptoUtils.SharedKey(ourSignedPreKey.Private, theirRatchetKey);
            byte[] ratchetMasterKey = CryptoUtils.Hkdf(ratchetSharedSecret, 64, rootKey, "WhisperRatchet");
            
            byte[] finalRootKey = ratchetMasterKey.Take(32).ToArray();
            byte[] chainKey = ratchetMasterKey.Skip(32).Take(32).ToArray();
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Initial ChainKey: {BitConverter.ToString(chainKey.Take(4).ToArray())}...");

            // Store session - This is a RECEIVE-ONLY session
            // When we receive a pkmsg, we establish a session for decrypting THEIR messages to US.
            // This does NOT allow us to send to them - we need their prekey bundle for that.
            // HasSession will return false for this session (CanSend=false), forcing prekey bundle fetch.
            
            SessionData priorSession = null;
            lock (_sessionLock)
            {
                if (_authState.Sessions.TryGetValue(senderJid, out var existingBytes) && existingBytes != null)
                {
                    try
                    {
                        priorSession = NormalizeSessionData(JsonConvert.DeserializeObject<SessionData>(System.Text.Encoding.UTF8.GetString(existingBytes)));
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to parse existing session for {senderJid} during pkmsg merge: {ex.Message}");
                    }
                }
            }

            bool preserveSendState = priorSession?.CanSend == true &&
                priorSession.SendingChainKey != null &&
                priorSession.OurSendingRatchetPrivate != null &&
                priorSession.OurSendingRatchetPublic != null;

            var session = new SessionData
            {
                RootKey = finalRootKey,
                ChainKey = chainKey,
                Counter = 0,
                TheirIdentityPublicKey = theirIdentityKey,
                OurRatchetPrivateKey = ourSignedPreKey.Private,
                TheirRatchetPublicKey = theirRatchetKey,
                SkippedMessageKeys = priorSession?.SkippedMessageKeys?.ToList() ?? new List<SkippedMessageKeyState>(),
                CanSend = preserveSendState,
                PreviousSessions = BuildPreviousSessionRing(priorSession)
            };

            if (session.PreviousSessions.Count > 0)
            {
                WhatsAppService.Log($"[Signal] Archived {session.PreviousSessions.Count} previous session(s) while accepting pkmsg for {senderJid}");
            }

            if (preserveSendState)
            {
                session.SendingChainKey = priorSession.SendingChainKey;
                session.SendingCounter = priorSession.SendingCounter;
                session.PreviousSendingCounter = priorSession.PreviousSendingCounter;
                session.OurSendingRatchetPrivate = priorSession.OurSendingRatchetPrivate;
                session.OurSendingRatchetPublic = priorSession.OurSendingRatchetPublic;
                session.IsPendingPreKey = priorSession.IsPendingPreKey;
                session.PendingSignedPreKeyId = priorSession.PendingSignedPreKeyId;
                session.PendingPreKeyId = priorSession.PendingPreKeyId;
                session.PendingBaseKey = priorSession.PendingBaseKey;
                session.RegistrationId = priorSession.RegistrationId;
                WhatsAppService.Log($"[Signal] Preserving send-capable session state while merging incoming pkmsg for {senderJid}");
            }
            

            
            // 6. Decrypt the payload
            var plaintext = DecryptPayload(signalMsg, session, senderJid);
            if (plaintext != null)
            {
                // Save updated session ONLY if decryption was successful
                var updatedSessionJson = Newtonsoft.Json.JsonConvert.SerializeObject(session);
                var updatedBytes = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
                lock (_sessionLock)
                {
                    _authState.Sessions[senderJid] = updatedBytes;
                }
                if (_keyStore != null)
                {
                    try
                    {
                        _keyStore.SetSessionAsync(senderJid, updatedBytes).Wait();
                        WhatsAppService.Log($"[Signal] Persisted pkmsg-established session for {senderJid}");
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to persist pkmsg-established session for {senderJid}: {ex.Message}");
                    }
                }
                WhatsAppService.Log($"[Signal] Session established and saved for {senderJid} (CanSend={session.CanSend})");
            }
            
            return plaintext;
        }


        private byte[] TryDecryptWithSessionData(SignalMessage msg, SessionData session, string candidate, string attemptLabel)
        {
            byte[] msgRatchetKey = msg.HasRatchetKey ? NormalizeCurve25519PubKey(msg.RatchetKey.ToByteArray()) : null;
            byte[] sessionRatchetKey = NormalizeCurve25519PubKey(session.TheirRatchetPublicKey);
            if (VerboseSignalLogging)
            {
                WhatsAppService.Log($"[Signal] Session candidate={candidate}, attempt={attemptLabel}, root={Fingerprint(session.RootKey)}, chain={Fingerprint(session.ChainKey)}, sessionRatchet={Fingerprint(sessionRatchetKey)}, msgRatchet={Fingerprint(msgRatchetKey)}, counter={session.Counter}, msgCounter={msg.Counter}");
            }

            byte[] plaintext = TryDecryptFromSkippedMessageKey(msg, session, msgRatchetKey, candidate);
            if (plaintext != null)
            {
                WhatsAppService.Log($"[Signal] Direct skipped-key cache hit for {candidate} ({attemptLabel}): counter={msg.Counter}, ratchet={Fingerprint(msgRatchetKey ?? sessionRatchetKey)}");
            }
            else
            {
                // Handle Ratchet Advancement (Forward Secrecy)
                if (msgRatchetKey != null && !(sessionRatchetKey ?? new byte[0]).SequenceEqual(msgRatchetKey))
                {
                    if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Advancing ratchet for {candidate} ({attemptLabel}): {Fingerprint(sessionRatchetKey)} -> {Fingerprint(msgRatchetKey)}");

                    byte[] theirNewRatchetKey = msgRatchetKey;

                    if (!CacheSkippedMessageKeys(session, sessionRatchetKey, session.Counter, msg.PreviousCounter, candidate, "previous receiving ratchet"))
                    {
                        WhatsAppService.Log($"[Signal] Decrypt failed for session candidate {candidate} ({attemptLabel}); previous-chain skip window too large");
                        return null;
                    }

                    // masterKey = HKDF(DH(theirNewRatchetKey, ourRatchetPrivateKey), RootKey, info="WhisperRatchet")
                    byte[] sharedSecret = CryptoUtils.SharedKey(session.OurRatchetPrivateKey, theirNewRatchetKey);
                    byte[] masterKey = CryptoUtils.Hkdf(sharedSecret, 64, session.RootKey, "WhisperRatchet");

                    session.RootKey = masterKey.Take(32).ToArray();
                    session.ChainKey = masterKey.Skip(32).Take(32).ToArray();
                    session.Counter = 0;
                    session.TheirRatchetPublicKey = theirNewRatchetKey;

                    // In Double Ratchet, when we step the receiving ratchet, track previous sending chain length
                    // and prepare to step the sending ratchet on next send.
                    session.PreviousSendingCounter = session.SendingCounter;
                    session.SendingChainKey = null; // Force EncryptMessage to step the sending ratchet

                    if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Ratchet advanced for {candidate} ({attemptLabel}). newRoot={Fingerprint(session.RootKey)}, newChain={Fingerprint(session.ChainKey)}");
                }
                else if (msgRatchetKey != null)
                {
                    // Keep stored key normalized even when no ratchet step is needed.
                    session.TheirRatchetPublicKey = sessionRatchetKey ?? msgRatchetKey;
                    if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Ratchet unchanged for {candidate} ({attemptLabel}): {Fingerprint(session.TheirRatchetPublicKey)}");
                }

                plaintext = DecryptPayload(msg, session, candidate);
            }

            if (plaintext == null)
            {
                WhatsAppService.Log($"[Signal] Decrypt failed for session candidate {candidate} ({attemptLabel}); session state not committed");
            }

            return plaintext;
        }

        private sealed class DirectSessionAttempt
        {
            public string Label { get; set; }
            public int PreviousIndex { get; set; }
            public SessionData Session { get; set; }
        }

        private byte[] DecryptWithExistingSession(SignalMessage msg, string senderJid)
        {
            var sessionCandidates = BuildSessionCandidatesForSender(senderJid);
            if (sessionCandidates.Count == 0)
            {
                WhatsAppService.Log($"[Signal] No session found for {senderJid}");
                return null;
            }

            var storedCandidates = new List<string>();
            lock (_sessionLock)
            {
                foreach (var candidate in sessionCandidates)
                {
                    if (_authState.Sessions.ContainsKey(candidate))
                    {
                        storedCandidates.Add(candidate);
                    }
                }
            }

            if (storedCandidates.Count == 0)
            {
                WhatsAppService.Log($"[Signal] No stored direct session for {senderJid}; candidates={string.Join(", ", sessionCandidates)}; me={_authState.Me?.Id}; meLid={_authState.Me?.Lid}");
                return null;
            }

            if (storedCandidates.Count != sessionCandidates.Count)
            {
                WhatsAppService.Log($"[Signal] Direct session candidates for {senderJid}: stored={string.Join(", ", storedCandidates)}; missing={string.Join(", ", sessionCandidates.Except(storedCandidates, StringComparer.OrdinalIgnoreCase))}");
            }

            foreach (var candidate in sessionCandidates)
            {
                byte[] sessionJson;
                lock (_sessionLock)
                {
                    if (!_authState.Sessions.TryGetValue(candidate, out sessionJson))
                    {
                        continue;
                    }
                }

                SessionData session;
                try
                {
                    var sessionJsonText = System.Text.Encoding.UTF8.GetString(sessionJson);
                    session = NormalizeSessionData(JsonConvert.DeserializeObject<SessionData>(sessionJsonText));
                    if (session == null)
                    {
                        continue;
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[Signal] Failed to parse session JSON for {candidate}: {ex.Message}");
                    continue;
                }

                var attempts = new List<DirectSessionAttempt>
                {
                    new DirectSessionAttempt
                    {
                        Label = "active",
                        PreviousIndex = -1,
                        Session = CloneSessionData(session, false)
                    }
                };

                if (session.PreviousSessions != null)
                {
                    for (int previousIndex = 0; previousIndex < session.PreviousSessions.Count; previousIndex++)
                    {
                        attempts.Add(new DirectSessionAttempt
                        {
                            Label = "previous-" + previousIndex,
                            PreviousIndex = previousIndex,
                            Session = CloneSessionData(session.PreviousSessions[previousIndex], false)
                        });
                    }
                }

                byte[] plaintext = null;
                SessionData sessionToPersist = null;

                foreach (var attempt in attempts)
                {
                    if (attempt.Session == null)
                    {
                        continue;
                    }

                    plaintext = TryDecryptWithSessionData(msg, attempt.Session, candidate, attempt.Label);
                    if (plaintext == null)
                    {
                        continue;
                    }

                    if (attempt.PreviousIndex >= 0)
                    {
                        session.PreviousSessions[attempt.PreviousIndex] = CloneSessionData(attempt.Session, false);
                        sessionToPersist = session;
                        WhatsAppService.Log($"[Signal] Decrypted with archived direct session for {candidate}: {attempt.Label}");
                    }
                    else
                    {
                        attempt.Session.PreviousSessions = session.PreviousSessions ?? new List<SessionData>();
                        sessionToPersist = attempt.Session;
                    }

                    break;
                }

                if (plaintext == null || sessionToPersist == null)
                {
                    continue;
                }

                // Save updated session state (chain advancement and ratchet)
                var updatedSessionJson = JsonConvert.SerializeObject(sessionToPersist);
                byte[] updatedBytes = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
                lock (_sessionLock)
                {
                    _authState.Sessions[candidate] = updatedBytes;
                    // If we decrypted via an alias/device variant, memoize under original senderJid too.
                    if (!string.Equals(candidate, senderJid, StringComparison.OrdinalIgnoreCase))
                    {
                        _authState.Sessions[senderJid] = updatedBytes;
                    }
                }

                if (_keyStore != null)
                {
                    try
                    {
                        _keyStore.SetSessionAsync(candidate, updatedBytes).Wait();
                        if (!string.Equals(candidate, senderJid, StringComparison.OrdinalIgnoreCase))
                        {
                            _keyStore.SetSessionAsync(senderJid, updatedBytes).Wait();
                        }
                        WhatsAppService.Log($"[Signal] Persisted successful decrypt session for {candidate}" +
                            (string.Equals(candidate, senderJid, StringComparison.OrdinalIgnoreCase) ? "" : $" (and memoized {senderJid})"));
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to persist decrypt session for {candidate}: {ex.Message}");
                    }
                }

                return plaintext;
            }

            return null;
        }

        private SenderKeyState LoadOrCreateLocalSenderKeyState(string groupJid, string senderJid)
        {
            bool createdNewSenderKey;
            return LoadOrCreateLocalSenderKeyState(groupJid, senderJid, out createdNewSenderKey);
        }

        private SenderKeyState LoadOrCreateLocalSenderKeyState(string groupJid, string senderJid, out bool createdNewSenderKey)
        {
            var existingState = LoadExistingLocalSenderKeyState(groupJid, senderJid);
            if (existingState != null)
            {
                createdNewSenderKey = false;
                return existingState;
            }

            string key = $"sk:{groupJid}:{senderJid}";
            createdNewSenderKey = true;
            var signingKeyPair = CryptoUtils.GenerateKeyPair();
            var created = new SenderKeyState
            {
                KeyId = BitConverter.ToInt32(CryptoUtils.RandomBytes(4), 0) & 0x7FFFFFFF,
                Iteration = 0,
                ChainKey = CryptoUtils.RandomBytes(32),
                SigningKey = CryptoUtils.GenerateSignalPubKey(signingKeyPair.Public),
                SigningPrivateKey = signingKeyPair.Private,
                MessageKeys = new List<SenderMessageKeyState>()
            };

            PersistSenderKeyState(groupJid, senderJid, created, "local sender key init");
            return created;
        }

        private SenderKeyState LoadExistingLocalSenderKeyState(string groupJid, string senderJid)
        {
            string key = $"sk:{groupJid}:{senderJid}";
            byte[] stateBytes = null;

            lock (_sessionLock)
            {
                _authState.Sessions.TryGetValue(key, out stateBytes);
            }

            if (stateBytes != null)
            {
                try
                {
                    var existing = NormalizeSenderKeyState(JsonConvert.DeserializeObject<SenderKeyState>(System.Text.Encoding.UTF8.GetString(stateBytes)));
                    if (existing != null &&
                        existing.ChainKey != null &&
                        existing.SigningKey != null &&
                        existing.SigningPrivateKey != null)
                    {
                        return existing;
                    }
                }
                catch (Exception ex)
                {
                    WhatsAppService.Log($"[Signal] Failed to load existing local SenderKeyState for {key}: {ex.Message}");
                }
            }

            return null;
        }

        private void PersistSenderKeyState(string groupJid, string senderJid, SenderKeyState state, string reason, bool waitForDisk = true)
        {
            state = NormalizeSenderKeyState(state);
            string key = $"sk:{groupJid}:{senderJid}";
            string json = JsonConvert.SerializeObject(state);
            byte[] stateBytes = System.Text.Encoding.UTF8.GetBytes(json);

            lock (_sessionLock)
            {
                _authState.Sessions[key] = stateBytes;
            }

            if (_keyStore != null)
            {
                if (waitForDisk)
                {
                    try
                    {
                        _keyStore.SetSenderKeyAsync(groupJid, senderJid, stateBytes).Wait();
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to persist SenderKey state for {key}: {ex.Message}");
                    }
                }
                else
                {
                    QueueSenderKeyPersist(groupJid, senderJid, stateBytes, key);
                }
            }

        }

        private void QueueSenderKeyPersist(string groupJid, string senderJid, byte[] stateBytes, string key)
        {
            lock (_senderKeyPersistQueueLock)
            {
                _senderKeyPersistTails.TryGetValue(key, out var previous);
                if (previous == null)
                {
                    previous = Task.CompletedTask;
                }

                var next = previous.ContinueWith(async _ =>
                {
                    try
                    {
                        await _keyStore.SetSenderKeyAsync(groupJid, senderJid, stateBytes);
                    }
                    catch (Exception ex)
                    {
                        WhatsAppService.Log($"[Signal] Failed to persist SenderKey state for {key}: {ex.Message}");
                    }
                }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

                _senderKeyPersistTails[key] = next;
            }
        }

        private SenderKeyState NormalizeSenderKeyState(SenderKeyState state)
        {
            if (state == null)
            {
                return null;
            }

            if (state.MessageKeys == null)
            {
                state.MessageKeys = new List<SenderMessageKeyState>();
            }

            state.MessageKeys.RemoveAll(messageKey => messageKey == null || messageKey.Seed == null);
            if (state.MessageKeys.Count > SenderKeyState.MaxSenderMessageKeys)
            {
                state.MessageKeys.RemoveRange(0, state.MessageKeys.Count - SenderKeyState.MaxSenderMessageKeys);
            }

            return state;
        }

        private void AddSenderMessageKey(SenderKeyState state, int iteration, byte[] seed)
        {
            if (state == null || seed == null)
            {
                return;
            }

            NormalizeSenderKeyState(state);

            for (int index = 0; index < state.MessageKeys.Count; index++)
            {
                if (state.MessageKeys[index].Iteration == iteration)
                {
                    state.MessageKeys[index].Seed = seed;
                    return;
                }
            }

            state.MessageKeys.Add(new SenderMessageKeyState
            {
                Iteration = iteration,
                Seed = seed
            });

            if (state.MessageKeys.Count > SenderKeyState.MaxSenderMessageKeys)
            {
                state.MessageKeys.RemoveAt(0);
            }
        }

        private bool TryRemoveSenderMessageKey(SenderKeyState state, int iteration, out byte[] seed)
        {
            seed = null;
            if (state == null)
            {
                return false;
            }

            NormalizeSenderKeyState(state);

            for (int index = 0; index < state.MessageKeys.Count; index++)
            {
                var messageKey = state.MessageKeys[index];
                if (messageKey.Iteration == iteration)
                {
                    seed = messageKey.Seed;
                    state.MessageKeys.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        private byte[] DeriveSenderMessageKeySeed(byte[] chainKey)
        {
            return CryptoUtils.HmacSha256(new byte[] { 0x01 }, chainKey);
        }

        private byte[] AdvanceSenderChainKey(byte[] chainKey)
        {
            return CryptoUtils.HmacSha256(new byte[] { 0x02 }, chainKey);
        }

        private void DeriveSenderMessageCipherMaterial(byte[] seed, out byte[] iv, out byte[] cipherKey)
        {
            byte[] salt = new byte[32];
            byte[][] derived = CryptoUtils.DeriveSecrets(seed, salt, "WhisperGroup", 2);

            iv = new byte[16];
            Array.Copy(derived[0], 0, iv, 0, 16);

            cipherKey = new byte[32];
            Array.Copy(derived[0], 16, cipherKey, 0, 16);
            Array.Copy(derived[1], 0, cipherKey, 16, 16);
        }

        private static byte[] BuildSenderKeyDistributionMessageBytes(SenderKeyState state)
        {
            var proto = new Proto.SenderKeyDistributionMessage
            {
                Id = (uint)state.KeyId,
                Iteration = (uint)state.Iteration,
                ChainKey = ByteString.CopyFrom(state.ChainKey),
                SigningKey = ByteString.CopyFrom(state.SigningKey)
            };

            byte version = 0x33;
            byte[] payload = proto.ToByteArray();
            byte[] serialized = new byte[payload.Length + 1];
            serialized[0] = version;
            Array.Copy(payload, 0, serialized, 1, payload.Length);
            return serialized;
        }

        private static byte[] BuildSenderKeyMessageBytes(int keyId, int iteration, byte[] ciphertext, byte[] signingPrivateKey)
        {
            var proto = new Proto.SenderKeyMessage
            {
                Id = (uint)keyId,
                Iteration = (uint)iteration,
                Ciphertext = ByteString.CopyFrom(ciphertext)
            };

            byte version = 0x33;
            byte[] payload = proto.ToByteArray();
            byte[] signedPortion = new byte[payload.Length + 1];
            signedPortion[0] = version;
            Array.Copy(payload, 0, signedPortion, 1, payload.Length);

            byte[] signature = CryptoUtils.Sign(signingPrivateKey, signedPortion);
            byte[] serialized = new byte[signedPortion.Length + signature.Length];
            Array.Copy(signedPortion, serialized, signedPortion.Length);
            Array.Copy(signature, 0, serialized, signedPortion.Length, signature.Length);
            return serialized;
        }

        private List<string> BuildSessionCandidatesForSender(string senderJid)
        {
            var result = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddCandidate(string jid)
            {
                if (string.IsNullOrWhiteSpace(jid)) return;
                string normalized = WA.NormalizeDeviceJid(jid);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                if (seen.Add(normalized))
                {
                    result.Add(normalized);
                }
            }

            AddCandidate(senderJid);

            WA.JidDecode(senderJid, out var user, out var server, out var device);
            if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(server))
            {
                AddCandidate($"{user}@{server}");
                AddCandidate($"{user}:0@{server}");
                if (device > 0)
                {
                    AddCandidate($"{user}:{device}@{server}");
                }
            }

            AddOwnPnLidAliasCandidates(user, server, device, AddCandidate);

            return result;
        }

        private void AddOwnPnLidAliasCandidates(string senderUser, string senderServer, int senderDevice, Action<string> addCandidate)
        {
            if (string.IsNullOrWhiteSpace(senderUser) || string.IsNullOrWhiteSpace(senderServer) || addCandidate == null)
            {
                return;
            }

            WA.JidDecode(WA.NormalizeDeviceJid(_authState.Me?.Id), out var ownPnUser, out var ownPnServer, out _);
            WA.JidDecode(WA.NormalizeDeviceJid(_authState.Me?.Lid), out var ownLidUser, out var ownLidServer, out _);

            bool senderIsOwnLid =
                !string.IsNullOrWhiteSpace(ownLidUser) &&
                string.Equals(senderUser, ownLidUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(senderServer, ownLidServer, StringComparison.OrdinalIgnoreCase);
            bool senderIsOwnPn =
                !string.IsNullOrWhiteSpace(ownPnUser) &&
                string.Equals(senderUser, ownPnUser, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(senderServer, ownPnServer, StringComparison.OrdinalIgnoreCase);

            if (senderIsOwnLid && !string.IsNullOrWhiteSpace(ownPnUser) && !string.IsNullOrWhiteSpace(ownPnServer))
            {
                addCandidate(_authState.Me?.Lid);
                addCandidate($"{ownPnUser}@{ownPnServer}");
                addCandidate($"{ownPnUser}:0@{ownPnServer}");
                addCandidate(_authState.Me?.Id);
                if (senderDevice > 0)
                {
                    addCandidate($"{ownPnUser}:{senderDevice}@{ownPnServer}");
                }
            }
            else if (senderIsOwnPn && !string.IsNullOrWhiteSpace(ownLidUser) && !string.IsNullOrWhiteSpace(ownLidServer))
            {
                addCandidate(_authState.Me?.Id);
                addCandidate($"{ownLidUser}@{ownLidServer}");
                addCandidate($"{ownLidUser}:0@{ownLidServer}");
                addCandidate(_authState.Me?.Lid);
                if (senderDevice > 0)
                {
                    addCandidate($"{ownLidUser}:{senderDevice}@{ownLidServer}");
                }
            }
        }

        private SessionData NormalizeSessionData(SessionData session)
        {
            return NormalizeSessionData(session, true);
        }

        private SessionData NormalizeSessionData(SessionData session, bool includePreviousSessions)
        {
            if (session == null)
            {
                return null;
            }

            if (session.SkippedMessageKeys == null)
            {
                session.SkippedMessageKeys = new List<SkippedMessageKeyState>();
            }

            for (int index = session.SkippedMessageKeys.Count - 1; index >= 0; index--)
            {
                var skipped = session.SkippedMessageKeys[index];
                if (skipped == null || skipped.MessageKey == null || skipped.RatchetKey == null)
                {
                    session.SkippedMessageKeys.RemoveAt(index);
                    continue;
                }

                skipped.RatchetKey = NormalizeCurve25519PubKey(skipped.RatchetKey);
                if (skipped.RatchetKey == null)
                {
                    session.SkippedMessageKeys.RemoveAt(index);
                }
            }

            if (session.SkippedMessageKeys.Count > SessionData.MaxSkippedMessageKeys)
            {
                session.SkippedMessageKeys.RemoveRange(0, session.SkippedMessageKeys.Count - SessionData.MaxSkippedMessageKeys);
            }

            session.TheirRatchetPublicKey = NormalizeCurve25519PubKey(session.TheirRatchetPublicKey);

            if (includePreviousSessions)
            {
                if (session.PreviousSessions == null)
                {
                    session.PreviousSessions = new List<SessionData>();
                }

                for (int index = session.PreviousSessions.Count - 1; index >= 0; index--)
                {
                    var previous = NormalizeSessionData(session.PreviousSessions[index], false);
                    if (previous == null || previous.RootKey == null)
                    {
                        session.PreviousSessions.RemoveAt(index);
                        continue;
                    }

                    previous.PreviousSessions = null;
                    session.PreviousSessions[index] = previous;
                }

                if (session.PreviousSessions.Count > SessionData.MaxPreviousSessions)
                {
                    session.PreviousSessions.RemoveRange(SessionData.MaxPreviousSessions, session.PreviousSessions.Count - SessionData.MaxPreviousSessions);
                }
            }
            else
            {
                session.PreviousSessions = null;
            }

            return session;
        }

        private SessionData CloneSessionData(SessionData session, bool includePreviousSessions)
        {
            if (session == null)
            {
                return null;
            }

            var clone = JsonConvert.DeserializeObject<SessionData>(JsonConvert.SerializeObject(session));
            return NormalizeSessionData(clone, includePreviousSessions);
        }

        private List<SessionData> BuildPreviousSessionRing(SessionData priorSession)
        {
            var previous = new List<SessionData>();
            if (priorSession == null)
            {
                return previous;
            }

            var archivedCurrent = CloneSessionData(priorSession, false);
            if (archivedCurrent != null && archivedCurrent.RootKey != null)
            {
                previous.Add(archivedCurrent);
            }

            if (priorSession.PreviousSessions != null)
            {
                foreach (var archived in priorSession.PreviousSessions)
                {
                    var archivedClone = CloneSessionData(archived, false);
                    if (archivedClone != null && archivedClone.RootKey != null)
                    {
                        previous.Add(archivedClone);
                    }
                }
            }

            if (previous.Count > SessionData.MaxPreviousSessions)
            {
                previous.RemoveRange(SessionData.MaxPreviousSessions, previous.Count - SessionData.MaxPreviousSessions);
            }

            return previous;
        }

        private byte[] DeriveDirectMessageKey(byte[] chainKey)
        {
            return CryptoUtils.HmacSha256(new byte[] { 0x01 }, chainKey);
        }

        private byte[] AdvanceDirectChainKey(byte[] chainKey)
        {
            return CryptoUtils.HmacSha256(new byte[] { 0x02 }, chainKey);
        }

        private void DeriveDirectMessageCipherMaterial(byte[] messageKey, out byte[] iv, out byte[] cipherKey, out byte[] macKey)
        {
            byte[] keys = CryptoUtils.Hkdf(messageKey, 80, new byte[32], "WhisperMessageKeys");

            cipherKey = new byte[32];
            macKey = new byte[32];
            iv = new byte[16];

            Array.Copy(keys, 0, cipherKey, 0, 32);
            Array.Copy(keys, 32, macKey, 0, 32);
            Array.Copy(keys, 64, iv, 0, 16);
        }

        private void AddSkippedMessageKey(SessionData session, byte[] ratchetKey, uint counter, byte[] messageKey)
        {
            if (session == null || ratchetKey == null || messageKey == null)
            {
                return;
            }

            NormalizeSessionData(session);
            byte[] normalizedRatchetKey = NormalizeCurve25519PubKey(ratchetKey);
            if (normalizedRatchetKey == null)
            {
                return;
            }

            for (int index = 0; index < session.SkippedMessageKeys.Count; index++)
            {
                var skipped = session.SkippedMessageKeys[index];
                if (skipped.Counter == counter && skipped.RatchetKey.SequenceEqual(normalizedRatchetKey))
                {
                    skipped.MessageKey = messageKey;
                    return;
                }
            }

            session.SkippedMessageKeys.Add(new SkippedMessageKeyState
            {
                RatchetKey = normalizedRatchetKey,
                Counter = counter,
                MessageKey = messageKey
            });

            if (session.SkippedMessageKeys.Count > SessionData.MaxSkippedMessageKeys)
            {
                session.SkippedMessageKeys.RemoveAt(0);
            }
        }

        private bool TryGetSkippedMessageKey(SessionData session, byte[] ratchetKey, uint counter, out byte[] messageKey)
        {
            messageKey = null;
            if (session == null || ratchetKey == null)
            {
                return false;
            }

            NormalizeSessionData(session);
            byte[] normalizedRatchetKey = NormalizeCurve25519PubKey(ratchetKey);
            if (normalizedRatchetKey == null)
            {
                return false;
            }

            for (int index = 0; index < session.SkippedMessageKeys.Count; index++)
            {
                var skipped = session.SkippedMessageKeys[index];
                if (skipped.Counter == counter && skipped.RatchetKey.SequenceEqual(normalizedRatchetKey))
                {
                    messageKey = skipped.MessageKey;
                    return true;
                }
            }

            return false;
        }

        private bool TryRemoveSkippedMessageKey(SessionData session, byte[] ratchetKey, uint counter, out byte[] messageKey)
        {
            messageKey = null;
            if (session == null || ratchetKey == null)
            {
                return false;
            }

            NormalizeSessionData(session);
            byte[] normalizedRatchetKey = NormalizeCurve25519PubKey(ratchetKey);
            if (normalizedRatchetKey == null)
            {
                return false;
            }

            for (int index = 0; index < session.SkippedMessageKeys.Count; index++)
            {
                var skipped = session.SkippedMessageKeys[index];
                if (skipped.Counter == counter && skipped.RatchetKey.SequenceEqual(normalizedRatchetKey))
                {
                    messageKey = skipped.MessageKey;
                    session.SkippedMessageKeys.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        private bool CacheSkippedMessageKeys(SessionData session, byte[] ratchetKey, uint fromCounter, uint toCounter, string senderJid, string reason)
        {
            if (session == null || session.ChainKey == null || ratchetKey == null || toCounter <= fromCounter)
            {
                return true;
            }

            ulong gap = (ulong)toCounter - fromCounter;
            if (gap > SessionData.MaxSkippedMessageKeys)
            {
                WhatsAppService.Log($"[Signal] Direct skipped-key cache refused {gap} keys for {senderJid} ({reason}); max={SessionData.MaxSkippedMessageKeys}");
                return false;
            }

            byte[] currentChainKey = session.ChainKey;
            uint currentCounter = fromCounter;
            int cached = 0;
            while (currentCounter < toCounter)
            {
                AddSkippedMessageKey(session, ratchetKey, currentCounter, DeriveDirectMessageKey(currentChainKey));
                currentChainKey = AdvanceDirectChainKey(currentChainKey);
                currentCounter++;
                cached++;
            }

            session.ChainKey = currentChainKey;
            session.Counter = currentCounter;
            WhatsAppService.Log($"[Signal] Direct skipped-key cache stored {cached} keys for {senderJid} ({reason}), counters {fromCounter}..{toCounter - 1}");
            return true;
        }

        private byte[] TryDecryptFromSkippedMessageKey(SignalMessage msg, SessionData session, byte[] msgRatchetKey, string senderJid)
        {
            byte[] ratchetKey = msgRatchetKey ?? NormalizeCurve25519PubKey(session?.TheirRatchetPublicKey);
            byte[] messageKey;
            if (!TryGetSkippedMessageKey(session, ratchetKey, msg.Counter, out messageKey))
            {
                if (session != null && msg.Counter < session.Counter)
                {
                    WhatsAppService.Log($"[Signal] Direct skipped-key cache miss for old counter {msg.Counter} (current {session.Counter}) from {senderJid}");
                }
                return null;
            }

            byte[] plaintext = TryDecryptPayloadWithMessageKey(msg, messageKey, senderJid, $"skipped counter {msg.Counter}");
            if (plaintext == null)
            {
                WhatsAppService.Log($"[Signal] Direct skipped-key cache entry failed decrypt for {senderJid}: counter={msg.Counter}, ratchet={Fingerprint(ratchetKey)}");
                return null;
            }

            byte[] removedKey;
            TryRemoveSkippedMessageKey(session, ratchetKey, msg.Counter, out removedKey);
            return plaintext;
        }

        private byte[] TryDecryptPayloadWithMessageKey(SignalMessage msg, byte[] messageKey, string senderJid, string reason)
        {
            byte[] iv;
            byte[] cipherKey;
            byte[] macKey;
            DeriveDirectMessageCipherMaterial(messageKey, out iv, out cipherKey, out macKey);

            byte[] ciphertext = msg.Ciphertext.ToByteArray();
            try
            {
                var plaintext = CryptoUtils.AesCbcDecrypt(ciphertext, cipherKey, iv);
                WhatsAppService.Log($"[Signal] Successfully decrypted payload ({reason}) for {senderJid}: {plaintext.Length} bytes");
                return TrimSignalPlaintext(plaintext);
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Signal] AES decryption failed ({reason}) for {senderJid}: {ex.Message}");
                return null;
            }
        }

        private byte[] TrimSignalPlaintext(byte[] plaintext)
        {
            if (plaintext == null)
            {
                return null;
            }

            int actualLen = plaintext.Length;
            while (actualLen > 0 && plaintext[actualLen - 1] == 0)
            {
                actualLen--;
            }

            if (actualLen < plaintext.Length)
            {
                byte[] trimmed = new byte[actualLen];
                Array.Copy(plaintext, 0, trimmed, 0, actualLen);
                WhatsAppService.Log($"[Signal] Trimmed {plaintext.Length - actualLen} trailing zeros. New length: {actualLen}");
                return trimmed;
            }

            return plaintext;
        }

        private byte[] DecryptPayload(SignalMessage msg, SessionData session, string senderJid)
        {
            NormalizeSessionData(session);

            // Use local copies for advancement so we don't corrupt the stored session on temporary failure
            byte[] currentChainKey = session.ChainKey;
            uint currentCounter = session.Counter;
            byte[] ratchetKey = msg.HasRatchetKey
                ? NormalizeCurve25519PubKey(msg.RatchetKey.ToByteArray())
                : NormalizeCurve25519PubKey(session.TheirRatchetPublicKey);
            
            if (msg.Counter < currentCounter)
            {
                WhatsAppService.Log($"[Signal] Direct skipped-key cache miss for old counter {msg.Counter} (current {currentCounter}) from {senderJid}");
                return null;
            }

            if (msg.Counter - currentCounter > SessionData.MaxSkippedMessageKeys)
            {
                WhatsAppService.Log($"[Signal] Direct message counter gap too large for {senderJid}: current={currentCounter}, target={msg.Counter}, max={SessionData.MaxSkippedMessageKeys}");
                return null;
            }

            // Catch up to the message counter
            int cachedSkippedKeys = 0;
            for (uint i = currentCounter; i < msg.Counter; i++)
            {
                AddSkippedMessageKey(session, ratchetKey, i, DeriveDirectMessageKey(currentChainKey));
                cachedSkippedKeys++;
                currentChainKey = AdvanceDirectChainKey(currentChainKey);
                currentCounter++;
            }

            if (cachedSkippedKeys > 0)
            {
                WhatsAppService.Log($"[Signal] Direct skipped-key cache stored {cachedSkippedKeys} keys while ratcheting {senderJid} to counter {msg.Counter}");
            }
            
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal] Using ChainKey for Counter {msg.Counter}: {BitConverter.ToString(currentChainKey.Take(4).ToArray())}...");

            // 1. Derive Message Key from current (possibly advanced) Chain Key: HMAC-SHA256(key=ChainKey, data=0x01)
            byte[] msgKey = DeriveDirectMessageKey(currentChainKey);

            // 4. Decrypt with AES-CBC
            var plaintext = TryDecryptPayloadWithMessageKey(msg, msgKey, senderJid, $"counter {msg.Counter}");
            if (plaintext == null)
            {
                return null;
            }

            // --- SUCCESS: COMMIT STATE ADVANCEMENT ---
            // Advance chain key once more to be ready for the NEXT message
            session.ChainKey = AdvanceDirectChainKey(currentChainKey);
            session.Counter = currentCounter + 1;

            if (VerboseSignalLogging && plaintext.Length > 0)
            {
                var dumpStart = BitConverter.ToString(plaintext.Take(Math.Min(plaintext.Length, 32)).ToArray());
                WhatsAppService.Log($"[Signal] Plaintext hex dump (start): {dumpStart}");
            }

            return plaintext;
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
            lock (_sessionLock)
            {
            byte[] sessionJson;
                if (!_authState.Sessions.TryGetValue(recipientJid, out sessionJson))
                {
                    throw new InvalidOperationException($"No session found for {recipientJid}. Cannot encrypt.");
                }

            var sessionJsonText = System.Text.Encoding.UTF8.GetString(sessionJson);
            var session = NormalizeSessionData(JsonConvert.DeserializeObject<SessionData>(sessionJsonText));

            if (session == null || session.RootKey == null)
            {
                throw new InvalidOperationException($"Invalid session data for {recipientJid}");
            }

            WhatsAppService.Log($"[Signal] EncryptMessage for {recipientJid}");
            WhatsAppService.Log($"[Signal]   RootKey: {BitConverter.ToString(session.RootKey.Take(4).ToArray())}...");
            WhatsAppService.Log($"[Signal]   TheirRatchetKey: {(session.TheirRatchetPublicKey != null ? BitConverter.ToString(session.TheirRatchetPublicKey.Take(4).ToArray()) + "..." : "null")}");
            WhatsAppService.Log($"[Signal]   SendingChainKey: {(session.SendingChainKey != null ? BitConverter.ToString(session.SendingChainKey.Take(4).ToArray()) + "..." : "null")}");
            WhatsAppService.Log($"[Signal]   PreviousSendingCounter: {session.PreviousSendingCounter}");

            // Initialize sending chain if not yet established (first send or after a receiving ratchet step)
            if (session.SendingChainKey == null || session.OurSendingRatchetPublic == null)
            {
                WhatsAppService.Log($"[Signal] Initializing sending chain (ratchet step or first send)");
                
                // Track previous chain length for the header
                session.PreviousSendingCounter = session.SendingCounter;
                session.SendingCounter = 0;
                
                // Generate our new ephemeral ratchet key pair for sending
                var ourRatchetKeyPair = Crypto.CryptoUtils.GenerateKeyPair();
                session.OurSendingRatchetPrivate = ourRatchetKeyPair.Private;
                session.OurSendingRatchetPublic = ourRatchetKeyPair.Public;
                
                // In Double Ratchet, this new private key also becomes our receiving base for their NEXT ratchet step
                session.OurRatchetPrivateKey = ourRatchetKeyPair.Private;
                
                WhatsAppService.Log($"[Signal]   Generated OurSendingRatchet: {BitConverter.ToString(session.OurSendingRatchetPublic.Take(4).ToArray())}...");
                
                // DH: SharedSecret = DH(ourSendingPrivate, theirRatchetPublic)
                byte[] sharedSecret = Crypto.CryptoUtils.SharedKey(session.OurSendingRatchetPrivate, session.TheirRatchetPublicKey);
                WhatsAppService.Log($"[Signal]   DH SharedSecret (bytes 0-8): {BitConverter.ToString(sharedSecret.Take(8).ToArray())}...");
                
                // Derive sending chain from root key: DeriveSecrets(sharedSecret, RootKey, "WhisperRatchet")
                byte[][] ratchetKeys = Crypto.CryptoUtils.DeriveSecrets(sharedSecret, session.RootKey, "WhisperRatchet", 2);
                byte[] newRootKey = ratchetKeys[0];
                session.SendingChainKey = ratchetKeys[1];
                
                // Update root key for next ratchet step
                session.RootKey = newRootKey;
                
                WhatsAppService.Log($"[Signal]   New RootKey: {BitConverter.ToString(session.RootKey.Take(4).ToArray())}...");
                WhatsAppService.Log($"[Signal]   New SendingChainKey: {BitConverter.ToString(session.SendingChainKey.Take(4).ToArray())}...");
            }

            WhatsAppService.Log($"[Signal] Encrypting with SendingCounter={session.SendingCounter}");

            // 1. Pad the plaintext
            byte[] paddedPlaintext = PadRandomMax16(plaintext);

            // 2. Get current message key from sending chain key: HMAC-SHA256(key=SendingChainKey, data=0x01)
            byte[] msgKey = DeriveDirectMessageKey(session.SendingChainKey);
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal]   MessageKey: {BitConverter.ToString(msgKey.Take(8).ToArray())}...");

            // 3. Derive Cipher Key, Mac Key, IV: DeriveSecrets(MessageKey, salt=0, info="WhisperMessageKeys")
            byte[][] msgKeys = Crypto.CryptoUtils.DeriveSecrets(msgKey, new byte[32], "WhisperMessageKeys", 3);
            
            byte[] cipherKey = msgKeys[0];  // 32 bytes
            byte[] macKey = msgKeys[1];     // 32 bytes
            byte[] iv = new byte[16];
            Array.Copy(msgKeys[2], 0, iv, 0, 16);  // First 16 bytes of third key

            if (VerboseSignalLogging)
            {
                WhatsAppService.Log($"[Signal]   CipherKey: {BitConverter.ToString(cipherKey.Take(8).ToArray())}...");
                WhatsAppService.Log($"[Signal]   MacKey: {BitConverter.ToString(macKey.Take(8).ToArray())}...");
                WhatsAppService.Log($"[Signal]   IV: {BitConverter.ToString(iv)}");
            }

            // 4. Encrypt with AES-CBC
            byte[] ciphertext = Crypto.CryptoUtils.AesCbcEncrypt(paddedPlaintext, cipherKey, iv);

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
            if (VerboseSignalLogging) WhatsAppService.Log($"[Signal]   SignalMessage proto: {msgProto.Length} bytes, RatchetKey: {BitConverter.ToString(ratchetKey33.Take(4).ToArray())}...");

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
            
            WhatsAppService.Log($"[Signal]   MAC input: {macInput.Length} bytes (ourId + theirId + ver + proto)");
            
            byte[] fullMac = Crypto.CryptoUtils.HmacSha256(macInput, macKey);
            byte[] mac8 = new byte[8];
            Array.Copy(fullMac, 0, mac8, 0, 8);

            // Final result: version + protobuf + mac8
            byte[] result = new byte[1 + msgProto.Length + 8];
            result[0] = versionByte;
            Array.Copy(msgProto, 0, result, 1, msgProto.Length);
            Array.Copy(mac8, 0, result, 1 + msgProto.Length, 8);

            // 7. Advance sending chain key for next message: HMAC-SHA256(key=SendingChainKey, data=0x02)
            session.SendingChainKey = AdvanceDirectChainKey(session.SendingChainKey);
            session.SendingCounter++;

            // Save updated session
            // 9. If pending prekey, wrap in PreKeyWhisperMessage
            byte[] finalResult = result;
            string finalType = "msg";

            if (session.IsPendingPreKey)
            {
                WhatsAppService.Log($"[Signal] PKMSG Construction (STANDARD SIGNAL TAGS):");
                WhatsAppService.Log($"[Signal]   Using RegistrationId: {_authState.RegistrationId}");
                WhatsAppService.Log($"[Signal]   PreKeyId: {session.PendingPreKeyId}");
                WhatsAppService.Log($"[Signal]   SignedPreKeyId: {session.PendingSignedPreKeyId}");

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
                    
                    WhatsAppService.Log($"[Signal]   PreKeySignalMessage proto: {pkMsgProto.Length} bytes");
                    WhatsAppService.Log($"[Signal] PKMSG PROTO HEX (first 100): {BitConverter.ToString(pkMsgProto.Take(Math.Min(pkMsgProto.Length, 100)).ToArray())}");
                    
                    // Final result: version (0x33) for PreKeyWhisperMessage wrapper
                    byte pkVersionByte = (byte)((3 << 4) | 3); // 0x33
                    finalResult = new byte[1 + pkMsgProto.Length];
                    finalResult[0] = pkVersionByte;
                    Array.Copy(pkMsgProto, 0, finalResult, 1, pkMsgProto.Length);
                    
                    WhatsAppService.Log($"[Signal] PKMSG FINAL HEX (first 50): {BitConverter.ToString(finalResult.Take(Math.Min(finalResult.Length, 50)).ToArray())}");

                    finalType = "pkmsg";
                    session.IsPendingPreKey = false;
                }
            }

            var updatedSessionJson = JsonConvert.SerializeObject(session);
            lock (_sessionLock)
            {
                _authState.Sessions[recipientJid] = System.Text.Encoding.UTF8.GetBytes(updatedSessionJson);
            }

            WhatsAppService.Log($"[Signal] Encrypted message ({finalType}): {finalResult.Length} bytes, new SendingCounter={session.SendingCounter}");

            // Self-test: Verify that we can decrypt what we just encrypted
            if (finalType == "pkmsg")
            {
                VerifyDecryption(finalResult, session, paddedPlaintext);
            }

            return new EncryptResult { Type = finalType, Ciphertext = finalResult };
            }
        }

        private void VerifyDecryption(byte[] fullPacket, SessionData session, byte[] expectedPlaintext)
        {
            try
            {
                WhatsAppService.Log($"[Signal] === STARTING SELF-TEST DECRYPTION ===");
                
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
                WhatsAppService.Log($"[Signal] SELF-TEST FAIL: Could not find inner message tag 4");
                return;
            }
                
                // 3. Extract SignalMessage from innerPacket
                byte innerVersion = innerPacket[0];
                byte[] signalProto = new byte[innerPacket.Length - 1 - 8];
                Array.Copy(innerPacket, 1, signalProto, 0, signalProto.Length);
                var signalMsg = SignalMessage.Parser.ParseFrom(signalProto);
                
                // 4. Verification
                byte[] ciphertext = signalMsg.Ciphertext.ToByteArray();
                if (VerboseSignalLogging)
                {
                    WhatsAppService.Log($"[Signal] SELF-TEST: Inner version: {innerVersion:X2}");
                    WhatsAppService.Log($"[Signal] SELF-TEST: SignalMsg: Counter={signalMsg.Counter}, RatchetKey={BitConverter.ToString(signalMsg.RatchetKey.ToByteArray().Take(4).ToArray())}...");
                    WhatsAppService.Log($"[Signal] SELF-TEST: Ciphertext (first 8): {BitConverter.ToString(ciphertext.Take(8).ToArray())}");
                    WhatsAppService.Log($"[Signal] SELF-TEST: Expected Plaintext (first 8): {BitConverter.ToString(expectedPlaintext.Take(8).ToArray())}");
                }

                WhatsAppService.Log($"[Signal] === SELF-TEST COMPLETED ===");
            }
            catch (Exception ex)
            {
                WhatsAppService.Log($"[Signal] SELF-TEST ERROR: {ex.Message}");
            }
        }
    }
}

