using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Macs;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Paddings;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace Unison.UWPApp.Crypto
{
    /// <summary>
    /// Cryptographic utilities for WhatsApp protocol.
    /// All operations use BouncyCastle 1.8.5 which supports netstandard1.x
    /// </summary>
    public static class CryptoUtils
    {
        // Lazy initialization to avoid ARM crash during static initialization
        private static SecureRandom _random;
        private static SecureRandom Random => _random ?? (_random = new SecureRandom());
        
        // Key bundle type prefix for Signal protocol
        public static readonly byte[] KEY_BUNDLE_TYPE = new byte[] { 5 };

        #region Key Generation

        /// <summary>
        /// Generates a new X25519 key pair for Curve25519 ECDH
        /// </summary>
        public static KeyPair GenerateKeyPair()
        {
            var generator = new X25519KeyPairGenerator();
            generator.Init(new X25519KeyGenerationParameters(Random));
            var keyPair = generator.GenerateKeyPair();
            
            var privateKey = ((X25519PrivateKeyParameters)keyPair.Private).GetEncoded();
            var publicKey = ((X25519PublicKeyParameters)keyPair.Public).GetEncoded();
            
            return new KeyPair(privateKey, publicKey);
        }

        /// <summary>
        /// Generates an Ed25519 key pair for signing
        /// </summary>
        public static KeyPair GenerateSigningKeyPair()
        {
            var generator = new Ed25519KeyPairGenerator();
            generator.Init(new Ed25519KeyGenerationParameters(Random));
            var keyPair = generator.GenerateKeyPair();
            
            var privateKey = ((Ed25519PrivateKeyParameters)keyPair.Private).GetEncoded();
            var publicKey = ((Ed25519PublicKeyParameters)keyPair.Public).GetEncoded();
            
            return new KeyPair(privateKey, publicKey);
        }

        #endregion

        #region ECDH

        /// <summary>
        /// Computes shared secret using X25519 ECDH
        /// </summary>
        public static byte[] SharedKey(byte[] privateKey, byte[] publicKey)
        {
            // Add version byte prefix if needed (Signal format)
            if (publicKey.Length == 33 && publicKey[0] == 5)
            {
                var trimmed = new byte[32];
                Array.Copy(publicKey, 1, trimmed, 0, 32);
                publicKey = trimmed;
            }

            var privParams = new X25519PrivateKeyParameters(privateKey, 0);
            var pubParams = new X25519PublicKeyParameters(publicKey, 0);
            
            var agreement = new X25519Agreement();
            agreement.Init(privParams);
            
            var sharedSecret = new byte[agreement.AgreementSize];
            agreement.CalculateAgreement(pubParams, sharedSecret, 0);
            
            return sharedSecret;
        }

        #endregion

        #region XEdDSA Test

        /// <summary>
        /// Test XEdDSA signature comparison with Baileys.
        /// Uses same fixed test data to verify our XEdDSA matches Baileys output.
        /// </summary>
        public static string RunXEdDSATest()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== XEdDSA SIGNATURE COMPARISON TEST ===");
            
            // Fixed test data - matches Baileys xeddsa_test.ts
            byte[] testPrivateKey = Convert.FromBase64String("aCdSKYNL+MrUtAcUx9c/ZhEiM0RVZneImaq7zN3u/wA=");
            byte[] testMessage = Convert.FromBase64String("BT3GwpWAleC9LBT1/lVt1bPDRQtW2gGJC+5+IN6bRVNJ");
            
            // Expected Baileys signature (from xeddsa_test.ts output)
            string expectedBaileysBase64 = "lKxFbMbJJOHPWEewXUCgkuQeDHkXQpTscVVWc4lXHlSTvUPp7NOd7jBWWshVYx2phscEnol5mbszxLuNuZJcgg==";
            byte[] expectedBaileysSig = Convert.FromBase64String(expectedBaileysBase64);
            
            sb.AppendLine();
            sb.AppendLine("Test Private Key (32b): " + BitConverter.ToString(testPrivateKey, 0, 16).Replace("-", "-") + "...");
            sb.AppendLine("Test Message (33b): " + BitConverter.ToString(testMessage, 0, 16).Replace("-", "-") + "...");
            sb.AppendLine();
            
            // Sign with our XEdDSA
            byte[] uwpSignature = XEdDSA.Sign(testPrivateKey, testMessage);
            
            sb.AppendLine("UWP XEdDSA Signature:");
            sb.AppendLine("  Base64: " + Convert.ToBase64String(uwpSignature));
            sb.AppendLine("  Hex (first 32): " + BitConverter.ToString(uwpSignature, 0, 32));
            sb.AppendLine("  Hex (last 32): " + BitConverter.ToString(uwpSignature, 32, 32));
            sb.AppendLine();
            sb.AppendLine("Baileys Expected Signature:");
            sb.AppendLine("  Base64: " + expectedBaileysBase64);
            sb.AppendLine("  Hex (first 32): " + BitConverter.ToString(expectedBaileysSig, 0, 32));
            sb.AppendLine("  Hex (last 32): " + BitConverter.ToString(expectedBaileysSig, 32, 32));
            sb.AppendLine();
            
            // Compare
            bool match = uwpSignature.Length == expectedBaileysSig.Length;
            if (match)
            {
                for (int i = 0; i < uwpSignature.Length; i++)
                {
                    if (uwpSignature[i] != expectedBaileysSig[i])
                    {
                        match = false;
                        break;
                    }
                }
            }
            
            sb.AppendLine(match ? "MATCH: YES - XEdDSA implementations are identical!" : "MATCH: NO - XEdDSA implementations DIFFER!");
            sb.AppendLine("=== END XEdDSA TEST ===");
            
            return sb.ToString();
        }

        #endregion

        #region Signing

        /// <summary>
        /// Signs data using XEdDSA (Ed25519 signatures from X25519 keys).
        /// Uses pure C# implementation ported from BaileysCSharp for correct XEdDSA signatures.
        /// </summary>
        public static byte[] Sign(byte[] privateKey, byte[] data)
        {
            // Use XEdDSA implementation which correctly converts X25519 private key to Ed25519
            // and produces signatures compatible with Signal/WhatsApp protocol
            return XEdDSA.Sign(privateKey, data);
        }

        /// <summary>
        /// Verifies an XEdDSA signature using an X25519 public key
        /// </summary>
        public static bool Verify(byte[] publicKey, byte[] data, byte[] signature)
        {
            try
            {
                // Use the pure C# implementation which handles X25519 to Ed25519 public key conversion
                return XEdDSA.Verify(publicKey, data, signature);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Prefixes version byte to public key for Signal protocol compatibility
        /// </summary>
        public static byte[] GenerateSignalPubKey(byte[] pubKey)
        {
            if (pubKey.Length == 33)
                return pubKey;
            
            var result = new byte[33];
            result[0] = KEY_BUNDLE_TYPE[0];
            Array.Copy(pubKey, 0, result, 1, 32);
            return result;
        }

        #endregion

        #region AES-GCM

        private const int GCM_TAG_LENGTH = 16;
        private const int GCM_NONCE_LENGTH = 12;

        /// <summary>
        /// Encrypts using AES-256-GCM. Tag is appended to ciphertext.
        /// </summary>
        public static byte[] AesGcmEncrypt(byte[] plaintext, byte[] key, byte[] iv, byte[] additionalData)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), GCM_TAG_LENGTH * 8, iv, additionalData);
            cipher.Init(true, parameters);
            
            var ciphertext = new byte[cipher.GetOutputSize(plaintext.Length)];
            var len = cipher.ProcessBytes(plaintext, 0, plaintext.Length, ciphertext, 0);
            cipher.DoFinal(ciphertext, len);
            
            return ciphertext;
        }

        /// <summary>
        /// Decrypts AES-256-GCM. Expects tag appended to ciphertext.
        /// </summary>
        public static byte[] AesGcmDecrypt(byte[] ciphertext, byte[] key, byte[] iv, byte[] additionalData)
        {
            var cipher = new GcmBlockCipher(new AesEngine());
            var parameters = new AeadParameters(new KeyParameter(key), GCM_TAG_LENGTH * 8, iv, additionalData);
            cipher.Init(false, parameters);
            
            var plaintext = new byte[cipher.GetOutputSize(ciphertext.Length)];
            var len = cipher.ProcessBytes(ciphertext, 0, ciphertext.Length, plaintext, 0);
            cipher.DoFinal(plaintext, len);
            
            return plaintext;
        }

        #endregion

        #region AES-CBC

        /// <summary>
        /// Decrypts using AES-256-CBC with PKCS7 padding.
        /// </summary>
        public static byte[] AesCbcDecrypt(byte[] ciphertext, byte[] key, byte[] iv)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using (var decryptor = aes.CreateDecryptor())
                using (var ms = new System.IO.MemoryStream(ciphertext))
                using (var cs = new System.Security.Cryptography.CryptoStream(ms, decryptor, System.Security.Cryptography.CryptoStreamMode.Read))
                using (var output = new System.IO.MemoryStream())
                {
                    cs.CopyTo(output);
                    return output.ToArray();
                }
            }
        }

        /// <summary>
        /// Encrypts using AES-256-CBC with PKCS7 padding.
        /// </summary>
        public static byte[] AesCbcEncrypt(byte[] plaintext, byte[] key, byte[] iv)
        {
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = key;
                aes.IV = iv;
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;

                using (var encryptor = aes.CreateEncryptor())
                using (var ms = new System.IO.MemoryStream())
                using (var cs = new System.Security.Cryptography.CryptoStream(ms, encryptor, System.Security.Cryptography.CryptoStreamMode.Write))
                {
                    cs.Write(plaintext, 0, plaintext.Length);
                    cs.FlushFinalBlock();
                    return ms.ToArray();
                }
            }
        }

        #endregion

        #region Compression

        /// <summary>
        /// Decompresses Zlib data (skips 2-byte RFC 1950 header)
        /// </summary>
        public static byte[] DecompressZlib(byte[] data)
        {
            if (data == null || data.Length < 2) return null;

            // WhatsApp uses Zlib (RFC 1950), which has a 2-byte header
            // DeflateStream (RFC 1951) expects raw deflate data.
            // Header is usually 78 9C (Default), 78 01 (No compression), etc.
            using (var ms = new MemoryStream(data, 2, data.Length - 2))
            using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
            using (var output = new MemoryStream())
            {
                ds.CopyTo(output);
                return output.ToArray();
            }
        }

        #endregion

        #region AES-CTR

        /// <summary>
        /// Encrypts using AES-256-CTR
        /// </summary>
        public static byte[] AesCtrEncrypt(byte[] plaintext, byte[] key, byte[] iv)
        {
            var cipher = new SicBlockCipher(new AesEngine());
            cipher.Init(true, new ParametersWithIV(new KeyParameter(key), iv));
            
            var ciphertext = new byte[plaintext.Length];
            for (int i = 0; i < plaintext.Length; i += 16)
            {
                int blockSize = Math.Min(16, plaintext.Length - i);
                var block = new byte[16];
                Array.Copy(plaintext, i, block, 0, blockSize);
                
                var outBlock = new byte[16];
                cipher.ProcessBlock(block, 0, outBlock, 0);
                Array.Copy(outBlock, 0, ciphertext, i, blockSize);
            }
            
            return ciphertext;
        }

        /// <summary>
        /// Decrypts AES-256-CTR (same as encrypt for CTR mode)
        /// </summary>
        public static byte[] AesCtrDecrypt(byte[] ciphertext, byte[] key, byte[] iv)
        {
            return AesCtrEncrypt(ciphertext, key, iv);
        }

        #endregion

        #region HKDF

        /// <summary>
        /// HKDF key derivation using SHA-256
        /// </summary>
        public static byte[] Hkdf(byte[] ikm, int length, byte[] salt, byte[] info)
        {
            var hkdf = new HkdfBytesGenerator(new Sha256Digest());
            hkdf.Init(new HkdfParameters(ikm, salt ?? new byte[0], info ?? new byte[0]));
            
            var output = new byte[length];
            hkdf.GenerateBytes(output, 0, length);
            return output;
        }

        /// <summary>
        /// HKDF with string info parameter
        /// </summary>
        public static byte[] Hkdf(byte[] ikm, int length, byte[] salt, string info)
        {
            var infoBytes = string.IsNullOrEmpty(info) ? new byte[0] : Encoding.UTF8.GetBytes(info);
            return Hkdf(ikm, length, salt, infoBytes);
        }

        /// <summary>
        /// DeriveSecrets - Signal's HMAC-based key derivation matching BaileysCSharp
        /// Returns array of 32-byte keys derived from input
        /// </summary>
        public static byte[][] DeriveSecrets(byte[] input, byte[] salt, byte[] info, int chunks = 3)
        {
            var signed = new List<byte[]>();
            
            // PRK = HMAC(key=salt, data=input)
            var PRK = HmacSha256(input, salt);
            
            // First chunk: HMAC(PRK, info || 0x01)
            var infoArray = new byte[info.Length + 1 + 32];
            Array.Copy(info, 0, infoArray, 32, info.Length);
            infoArray[infoArray.Length - 1] = 1;
            
            // For first iteration, skip the initial 32 empty bytes
            var firstInput = new byte[info.Length + 1];
            Array.Copy(infoArray, 32, firstInput, 0, info.Length + 1);
            signed.Add(HmacSha256(firstInput, PRK));
            
            // Subsequent chunks: HMAC(PRK, previous_output || info || counter)
            if (chunks > 1)
            {
                Array.Copy(signed[signed.Count - 1], 0, infoArray, 0, 32);
                infoArray[infoArray.Length - 1] = 2;
                signed.Add(HmacSha256(infoArray, PRK));
            }
            if (chunks > 2)
            {
                Array.Copy(signed[signed.Count - 1], 0, infoArray, 0, 32);
                infoArray[infoArray.Length - 1] = 3;
                signed.Add(HmacSha256(infoArray, PRK));
            }

            return signed.ToArray();
        }

        /// <summary>
        /// DeriveSecrets with string info parameter
        /// </summary>
        public static byte[][] DeriveSecrets(byte[] input, byte[] salt, string info, int chunks = 3)
        {
            var infoBytes = string.IsNullOrEmpty(info) ? new byte[0] : Encoding.UTF8.GetBytes(info);
            return DeriveSecrets(input, salt, infoBytes, chunks);
        }

        #endregion

        #region HMAC & Hash

        /// <summary>
        /// HMAC-SHA256
        /// </summary>
        public static byte[] HmacSha256(byte[] data, byte[] key)
        {
            var hmac = new HMac(new Sha256Digest());
            hmac.Init(new KeyParameter(key));
            hmac.BlockUpdate(data, 0, data.Length);
            
            var result = new byte[hmac.GetMacSize()];
            hmac.DoFinal(result, 0);
            return result;
        }

        /// <summary>
        /// HMAC-SHA512
        /// </summary>
        public static byte[] HmacSha512(byte[] data, byte[] key)
        {
            var hmac = new HMac(new Sha512Digest());
            hmac.Init(new KeyParameter(key));
            hmac.BlockUpdate(data, 0, data.Length);
            
            var result = new byte[hmac.GetMacSize()];
            hmac.DoFinal(result, 0);
            return result;
        }

        /// <summary>
        /// SHA-256 hash
        /// </summary>
        public static byte[] Sha256(byte[] data)
        {
            var digest = new Sha256Digest();
            digest.BlockUpdate(data, 0, data.Length);
            
            var result = new byte[digest.GetDigestSize()];
            digest.DoFinal(result, 0);
            return result;
        }

        #endregion

        #region PBKDF2

        /// <summary>
        /// Derives key from pairing code using PBKDF2-HMAC-SHA256
        /// </summary>
        public static byte[] DerivePairingCodeKey(string pairingCode, byte[] salt)
        {
            var generator = new Pkcs5S2ParametersGenerator(new Sha256Digest());
            var codeBytes = Encoding.UTF8.GetBytes(pairingCode);
            
            // CRITICAL: Baileys uses 2 << 16 = 131072 iterations, NOT 2048!
            generator.Init(codeBytes, salt, 2 << 16); // 131072 iterations
            
            var keyParam = (KeyParameter)generator.GenerateDerivedParameters("AES", 256);
            return keyParam.GetKey();
        }

        #endregion

        #region Random

        /// <summary>
        /// Generates cryptographically secure random bytes
        /// </summary>
        public static byte[] RandomBytes(int length)
        {
            var bytes = new byte[length];
            Random.NextBytes(bytes);
            return bytes;
        }

        /// <summary>
        /// Generates a random registration ID (0-16383)
        /// </summary>
        public static int GenerateRegistrationId()
        {
            var bytes = RandomBytes(2);
            return ((bytes[0] << 8) | bytes[1]) & 0x3FFF;
        }

        #endregion

        #region Encoding

        // Crockford Base32 alphabet (no I, L, O, U to avoid confusion)
        private static readonly char[] CROCKFORD = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".ToCharArray();

        /// <summary>
        /// Converts bytes to Crockford Base32 string (used for pairing codes)
        /// </summary>
        public static string BytesToCrockford(byte[] bytes)
        {
            var result = new StringBuilder();
            ulong value = 0;
            int bits = 0;
            
            foreach (var b in bytes)
            {
                value = (value << 8) | b;
                bits += 8;
                
                while (bits >= 5)
                {
                    bits -= 5;
                    result.Append(CROCKFORD[(int)((value >> bits) & 0x1F)]);
                }
            }
            
            if (bits > 0)
            {
                result.Append(CROCKFORD[(int)((value << (5 - bits)) & 0x1F)]);
            }
            
            return result.ToString();
        }

        /// <summary>
        /// Decodes a big-endian byte array to a uint
        /// </summary>
        public static uint DecodeBigEndian(byte[] data)
        {
            if (data == null || data.Length == 0) return 0;
            uint result = 0;
            for (int i = 0; i < data.Length; i++)
            {
                result = (result << 8) | data[i];
            }
            return result;
        }

        #endregion
    }

    /// <summary>
    /// Represents a cryptographic key pair
    /// </summary>
    public class KeyPair
    {
        public byte[] Private { get; }
        public byte[] Public { get; }

        public KeyPair(byte[] privateKey, byte[] publicKey)
        {
            Private = privateKey;
            Public = publicKey;
        }
    }
}
