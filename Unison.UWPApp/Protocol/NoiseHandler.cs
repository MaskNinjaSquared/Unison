using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Unison.UWPApp.Client;
using Unison.UWPApp.Crypto;

namespace Unison.UWPApp.Protocol
{
    /// <summary>
    /// Implements Noise_XX_25519_AESGCM_SHA256 handshake and encryption.
    /// Based on Baileys noise-handler.ts
    /// </summary>
    public class NoiseHandler
    {
        // Noise protocol identifier
        public static readonly string NOISE_MODE = "Noise_XX_25519_AESGCM_SHA256\0\0\0\0";
        
        // WhatsApp noise header: "WA" + version bytes
        public static readonly byte[] NOISE_WA_HEADER = new byte[] { 87, 65, 6, 3 }; // "WA" + dict version 3

        // WhatsApp certificate serial for validation
        public const long WA_CERT_SERIAL = 0;

        private readonly KeyPair _keyPair;
        private readonly byte[] _noiseHeader;
        private readonly byte[] _routingInfo;
        
        private byte[] _hash;
        private byte[] _salt;
        private byte[] _encKey;
        private byte[] _decKey;
        private int _readCounter;
        private int _writeCounter;
        private readonly object _stateLock = new object();
        private bool _isFinished;
        private bool _sentIntro;
        private byte[] _inBytes = new byte[0];

        public bool IsFinished => _isFinished;

        public NoiseHandler(KeyPair keyPair, byte[] routingInfo = null)
        {
            _keyPair = keyPair;
            _noiseHeader = NOISE_WA_HEADER;
            _routingInfo = routingInfo;
            
            // Initialize hash from noise mode string (padded to 32 bytes)
            var modeBytes = System.Text.Encoding.UTF8.GetBytes(NOISE_MODE);
            _hash = modeBytes.Length == 32 ? modeBytes : CryptoUtils.Sha256(modeBytes);
            _salt = _hash;
            _encKey = _hash;
            _decKey = _hash;
            _readCounter = 0;
            _writeCounter = 0;
            _isFinished = false;
            _sentIntro = false;

            // Authenticate initial data
            Authenticate(_noiseHeader);
            Authenticate(_keyPair.Public);

            Debug.WriteLine($"[Noise] Initialized with public key: {BitConverter.ToString(_keyPair.Public).Replace("-", "").Substring(0, 16)}...");
        }

        /// <summary>
        /// Mixes data into the hash (before handshake completion)
        /// </summary>
        public void Authenticate(byte[] data)
        {
            lock (_stateLock)
            {
                if (!_isFinished)
                {
                    var combined = new byte[_hash.Length + data.Length];
                    Array.Copy(_hash, 0, combined, 0, _hash.Length);
                    Array.Copy(data, 0, combined, _hash.Length, data.Length);
                    _hash = CryptoUtils.Sha256(combined);
                    Debug.WriteLine($"[Noise] Authenticated {data.Length} bytes, hash: {BitConverter.ToString(_hash).Replace("-", "").Substring(0, 16)}...");
                }
            }
        }

        /// <summary>
        /// Generates IV from counter for AES-GCM
        /// </summary>
        private byte[] GenerateIV(int counter)
        {
            var iv = new byte[12];
            // Counter is stored in big-endian in the last 4 bytes
            iv[8] = (byte)(counter >> 24);
            iv[9] = (byte)(counter >> 16);
            iv[10] = (byte)(counter >> 8);
            iv[11] = (byte)counter;
            return iv;
        }

        /// <summary>
        /// Encrypts plaintext using current encryption key
        /// </summary>
        public byte[] Encrypt(byte[] plaintext)
        {
            lock (_stateLock)
            {
                var iv = GenerateIV(_writeCounter);
                var result = CryptoUtils.AesGcmEncrypt(plaintext, _encKey, iv, _hash);
                _writeCounter++;
                AuthenticateLocked(result);
                Debug.WriteLine($"[Noise] Encrypted {plaintext.Length} bytes -> {result.Length} bytes, counter: {_writeCounter}");
                return result;
            }
        }

        private void AuthenticateLocked(byte[] data)
        {
            if (!_isFinished)
            {
                var combined = new byte[_hash.Length + data.Length];
                Array.Copy(_hash, 0, combined, 0, _hash.Length);
                Array.Copy(data, 0, combined, _hash.Length, data.Length);
                _hash = CryptoUtils.Sha256(combined);
            }
        }

        /// <summary>
        /// Decrypts ciphertext using current decryption key
        /// </summary>
        public byte[] Decrypt(byte[] ciphertext)
        {
            lock (_stateLock)
            {
                // Before handshake completion, use same counter
                var iv = GenerateIV(_isFinished ? _readCounter : _writeCounter);
                var result = CryptoUtils.AesGcmDecrypt(ciphertext, _decKey, iv, _hash);

                if (_isFinished)
                {
                    _readCounter++;
                }
                else
                {
                    _writeCounter++;
                }

                AuthenticateLocked(ciphertext);
                Debug.WriteLine($"[Noise] Decrypted {ciphertext.Length} bytes -> {result.Length} bytes");
                return result;
            }
        }

        /// <summary>
        /// HKDF key derivation and state update
        /// </summary>
        private void LocalHKDF(byte[] data, out byte[] writeKey, out byte[] readKey)
        {
            var key = CryptoUtils.Hkdf(data, 64, _salt, (byte[])null);
            writeKey = new byte[32];
            readKey = new byte[32];
            Array.Copy(key, 0, writeKey, 0, 32);
            Array.Copy(key, 32, readKey, 0, 32);
        }

        /// <summary>
        /// Mixes shared secret into encryption keys
        /// </summary>
        public void MixIntoKey(byte[] data)
        {
            lock (_stateLock)
            {
                LocalHKDF(data, out var write, out var read);
                _salt = write;
                _encKey = read;
                _decKey = read;
                _readCounter = 0;
                _writeCounter = 0;
                Debug.WriteLine($"[Noise] Mixed key, new encKey: {BitConverter.ToString(_encKey).Replace("-", "").Substring(0, 16)}...");
            }
        }

        /// <summary>
        /// Completes handshake initialization
        /// </summary>
        public void FinishInit()
        {
            lock (_stateLock)
            {
                LocalHKDF(new byte[0], out var write, out var read);
                _encKey = write;
                _decKey = read;
                _hash = new byte[0];
                _readCounter = 0;
                _writeCounter = 0;
                _isFinished = true;
                Debug.WriteLine("[Noise] Handshake finished, encrypted channel established");
            }
        }

        /// <summary>
        /// Processes server hello and completes XX handshake.
        /// Returns encrypted noise public key to send back.
        /// </summary>
        public byte[] ProcessHandshake(byte[] serverEphemeral, byte[] serverStaticEncrypted, byte[] serverPayloadEncrypted, KeyPair noiseKey)
        {
            Debug.WriteLine($"[Noise] Processing handshake...");
            Debug.WriteLine($"[Noise] Server ephemeral: {serverEphemeral.Length} bytes");
            Debug.WriteLine($"[Noise] Server static encrypted: {serverStaticEncrypted.Length} bytes");
            Debug.WriteLine($"[Noise] Server payload encrypted: {serverPayloadEncrypted.Length} bytes");

            // *** LOG NOISE KEYS FOR DEBUGGING ***
            SessionLogger.Instance.LogKeyInfo("Noise Handshake Inputs", new System.Collections.Generic.Dictionary<string, string>
            {
                { "ClientEphemeral.Public", Convert.ToBase64String(_keyPair.Public) },
                { "ServerEphemeral", Convert.ToBase64String(serverEphemeral) },
                { "NoiseKey.Public", Convert.ToBase64String(noiseKey.Public) },
                { "Hash (before)", Convert.ToBase64String(_hash) },
                { "EncKey (before)", Convert.ToBase64String(_encKey) }
            });

            // 1. Authenticate server ephemeral
            Authenticate(serverEphemeral);
            
            // 2. DH with our ephemeral and server ephemeral
            var sharedEE = CryptoUtils.SharedKey(_keyPair.Private, serverEphemeral);
            MixIntoKey(sharedEE);
            
            SessionLogger.Instance.LogKeyInfo("After DH(ee)", new System.Collections.Generic.Dictionary<string, string>
            {
                { "sharedEE", Convert.ToBase64String(sharedEE) },
                { "Hash", Convert.ToBase64String(_hash) },
                { "EncKey", Convert.ToBase64String(_encKey) }
            });
            
            // 3. Decrypt server static key
            var serverStatic = Decrypt(serverStaticEncrypted);
            Debug.WriteLine($"[Noise] Decrypted server static: {serverStatic.Length} bytes");
            
            // 4. DH with our ephemeral and server static
            var sharedES = CryptoUtils.SharedKey(_keyPair.Private, serverStatic);
            MixIntoKey(sharedES);
            
            SessionLogger.Instance.LogKeyInfo("After DH(es)", new System.Collections.Generic.Dictionary<string, string>
            {
                { "ServerStatic", Convert.ToBase64String(serverStatic) },
                { "sharedES", Convert.ToBase64String(sharedES) },
                { "Hash", Convert.ToBase64String(_hash) },
                { "EncKey", Convert.ToBase64String(_encKey) }
            });
            
            // 5. Decrypt certificate payload
            var certPayload = Decrypt(serverPayloadEncrypted);
            Debug.WriteLine($"[Noise] Decrypted cert payload: {certPayload.Length} bytes");
            
            // TODO: Validate certificate chain
            // For now, we trust the server certificate
            
            // 6. Encrypt our noise public key
            var keyEnc = Encrypt(noiseKey.Public);
            Debug.WriteLine($"[Noise] Encrypted noise key: {keyEnc.Length} bytes");
            
            // 7. DH with noise key and server ephemeral
            var sharedSE = CryptoUtils.SharedKey(noiseKey.Private, serverEphemeral);
            MixIntoKey(sharedSE);
            
            SessionLogger.Instance.LogKeyInfo("After DH(se) - Final", new System.Collections.Generic.Dictionary<string, string>
            {
                { "sharedSE", Convert.ToBase64String(sharedSE) },
                { "Hash (final)", Convert.ToBase64String(_hash) },
                { "EncKey (final)", Convert.ToBase64String(_encKey) }
            });
            
            return keyEnc;
        }

        /// <summary>
        /// Encodes a frame with length prefix and optional intro header
        /// </summary>
        public byte[] EncodeFrame(byte[] data)
        {
            if (_isFinished)
            {
                data = Encrypt(data);
            }

            byte[] header;
            if (_routingInfo != null && _routingInfo.Length > 0)
            {
                header = new byte[7 + _routingInfo.Length + _noiseHeader.Length];
                header[0] = (byte)'E';
                header[1] = (byte)'D';
                header[2] = 0;
                header[3] = 1;
                header[4] = (byte)(_routingInfo.Length >> 16);
                header[5] = (byte)(_routingInfo.Length >> 8);
                header[6] = (byte)_routingInfo.Length;
                Array.Copy(_routingInfo, 0, header, 7, _routingInfo.Length);
                Array.Copy(_noiseHeader, 0, header, 7 + _routingInfo.Length, _noiseHeader.Length);
            }
            else
            {
                header = _noiseHeader;
            }

            int introSize = _sentIntro ? 0 : header.Length;
            var frame = new byte[introSize + 3 + data.Length];

            if (!_sentIntro)
            {
                Array.Copy(header, 0, frame, 0, header.Length);
                _sentIntro = true;
            }

            // 3-byte big-endian length prefix
            frame[introSize] = (byte)(data.Length >> 16);
            frame[introSize + 1] = (byte)(data.Length >> 8);
            frame[introSize + 2] = (byte)data.Length;
            Array.Copy(data, 0, frame, introSize + 3, data.Length);

            Debug.WriteLine($"[Noise] Encoded frame: {frame.Length} bytes (data: {data.Length}, intro: {introSize})");
            return frame;
        }

        /// <summary>
        /// Decodes incoming data into frames
        /// </summary>
        public async Task DecodeFrame(byte[] newData, Func<byte[], Task> onFrame)
        {
            if (newData != null)
            {
                lock (_stateLock)
                {
                    // Append new data to buffer
                    var combined = new byte[_inBytes.Length + newData.Length];
                    Array.Copy(_inBytes, 0, combined, 0, _inBytes.Length);
                    Array.Copy(newData, 0, combined, _inBytes.Length, newData.Length);
                    _inBytes = combined;
                    Debug.WriteLine($"[Noise] Received {newData.Length} bytes, total buffer: {_inBytes.Length} bytes");
                }
            }

            while (true)
            {
                byte[] frameToProcess = null;

                lock (_stateLock)
                {
                    if (_inBytes.Length < 3)
                        return;

                    int size = (_inBytes[0] << 16) | (_inBytes[1] << 8) | _inBytes[2];

                    if (_inBytes.Length < size + 3)
                    {
                        Debug.WriteLine($"[Noise] Waiting for more data, need {size + 3}, have {_inBytes.Length}");
                        return;
                    }

                    frameToProcess = new byte[size];
                    Array.Copy(_inBytes, 3, frameToProcess, 0, size);

                    // Remove processed data from buffer
                    var remaining = new byte[_inBytes.Length - size - 3];
                    Array.Copy(_inBytes, size + 3, remaining, 0, remaining.Length);
                    _inBytes = remaining;
                }

                if (_isFinished)
                {
                    frameToProcess = Decrypt(frameToProcess);
                }

                if (frameToProcess != null)
                {
                    Debug.WriteLine($"[Noise] Decoded frame: {frameToProcess.Length} bytes");
                    await onFrame(frameToProcess);
                }
            }
        }
    }
}
