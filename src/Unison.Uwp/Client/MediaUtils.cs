using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.Baileys.Crypto; // For CryptoUtils
using System.Text;

namespace Unison.Uwp.Client
{
    public static class MediaUtils
    {
        // HKDF Info strings for different media types
        public const string IMAGE_HKDF_INFO = "WhatsApp Image Keys";
        public const string VIDEO_HKDF_INFO = "WhatsApp Video Keys";
        public const string AUDIO_HKDF_INFO = "WhatsApp Audio Keys";
        public const string DOCUMENT_HKDF_INFO = "WhatsApp Document Keys";
        public const string APP_STATE_HKDF_INFO = "WhatsApp App State Keys";
        public const string HISTORY_HKDF_INFO = "WhatsApp History Keys";

        public struct MediaKeys
        {
            public byte[] IV { get; set; }
            public byte[] CipherKey { get; set; }
            public byte[] MacKey { get; set; }
        }

        public struct EncryptedMediaResult
        {
            public byte[] MediaKey { get; set; }
            public byte[] EncryptedBytes { get; set; } // Contains Body + MAC
            public byte[] Mac { get; set; }
            public byte[] FileSha256 { get; set; }
            public byte[] FileEncSha256 { get; set; }
            public long FileLength { get; set; }
        }

        public static MediaKeys GetMediaKeys(byte[] mediaKey, string mediaType)
        {
            // Expand using HKDF to 112 bytes
            // Info depends on media type
            string infoStr = IMAGE_HKDF_INFO;
            switch (mediaType)
            {
                case "video":
                case "gif":
                    infoStr = VIDEO_HKDF_INFO;
                    break;
                case "audio":
                case "ptt":
                    infoStr = AUDIO_HKDF_INFO;
                    break;
                case "document":
                    infoStr = DOCUMENT_HKDF_INFO;
                    break;
                case "md-app-state":
                    infoStr = APP_STATE_HKDF_INFO;
                    break;
                case "md-msg-hist":
                    infoStr = HISTORY_HKDF_INFO;
                    break;
                case "sticker":
                case "image":
                default:
                    // Stickers use the same HKDF info as images (Baileys MEDIA_HKDF_KEY_MAPPING).
                    infoStr = IMAGE_HKDF_INFO;
                    break;
            }

            byte[] expanded = CryptoUtils.Hkdf(mediaKey, 112, null, infoStr);

            // iv: 0-16, cipherKey: 16-48, macKey: 48-80
            var keys = new MediaKeys();
            keys.IV = new byte[16];
            keys.CipherKey = new byte[32];
            keys.MacKey = new byte[32];

            Array.Copy(expanded, 0, keys.IV, 0, 16);
            Array.Copy(expanded, 16, keys.CipherKey, 0, 32);
            Array.Copy(expanded, 48, keys.MacKey, 0, 32);

            return keys;
        }

        public static async Task<EncryptedMediaResult> EncryptMediaAsync(byte[] fileBytes, string mediaType)
        {
            var result = new EncryptedMediaResult();
            result.FileLength = fileBytes.Length;
            
            // 1. Generate Media Key (32 random bytes)
            result.MediaKey = CryptoUtils.RandomBytes(32);

            // 2. Derive keys
            var keys = GetMediaKeys(result.MediaKey, mediaType);

            // 3. Calculate SHA256 of plaintext
            result.FileSha256 = CryptoUtils.Sha256(fileBytes);

            // 4. Encrypt using AES-CBC
            // Note: WhatsApp uses AES-CBC with PKCS7 padding.
            // Our CryptoUtils.AesCbcEncrypt handles this.
            byte[] encryptedBody = CryptoUtils.AesCbcEncrypt(fileBytes, keys.CipherKey, keys.IV);

            // 5. Calculate MAC without allocating a second IV+body buffer.
            byte[] fullMac = CryptoUtils.HmacSha256(
                keys.IV, encryptedBody, 0, encryptedBody.Length, keys.MacKey);
            result.Mac = new byte[10];
            Array.Copy(fullMac, 0, result.Mac, 0, 10);

            // 6. Final Bundle: EncryptedBody + MAC
            result.EncryptedBytes = new byte[encryptedBody.Length + 10];
            Array.Copy(encryptedBody, 0, result.EncryptedBytes, 0, encryptedBody.Length);
            Array.Copy(result.Mac, 0, result.EncryptedBytes, encryptedBody.Length, 10);

            // 7. Calculate EncSHA256 (SHA256 of EncryptedBytes)
            result.FileEncSha256 = CryptoUtils.Sha256(result.EncryptedBytes);

            return result;
        }

        /// <summary>
        /// Decrypts WhatsApp media payload using Baileys-compatible key derivation:
        /// HKDF(mediaKey) -> IV/CipherKey/MacKey, verify 10-byte MAC, then AES-CBC decrypt.
        /// </summary>
        public static byte[] DecryptMedia(byte[] encryptedBytes, byte[] mediaKey, string mediaType, byte[] expectedFileEncSha256 = null)
        {
            if (encryptedBytes == null || encryptedBytes.Length <= 10)
                throw new ArgumentException("Encrypted media payload is too short", nameof(encryptedBytes));
            if (mediaKey == null || mediaKey.Length == 0)
                throw new ArgumentException("Media key missing", nameof(mediaKey));

            if (expectedFileEncSha256 != null && expectedFileEncSha256.Length > 0)
            {
                var actualEncHash = CryptoUtils.Sha256(encryptedBytes);
                if (!FixedTimeEquals(actualEncHash, expectedFileEncSha256))
                    throw new Exception("Encrypted media SHA256 mismatch");
            }

            var keys = GetMediaKeys(mediaKey, mediaType);

            int cipherLen = encryptedBytes.Length - 10;
            var msgMac = new byte[10];
            Array.Copy(encryptedBytes, cipherLen, msgMac, 0, 10);

            // Stream the HMAC over IV + the ciphertext segment. The old implementation
            // allocated both a ciphertext copy and a second IV+ciphertext copy, tripling
            // peak memory for large history-sync blobs.
            var fullMac = CryptoUtils.HmacSha256(keys.IV, encryptedBytes, 0, cipherLen, keys.MacKey);

            var expectedMac = new byte[10];
            Array.Copy(fullMac, 0, expectedMac, 0, 10);
            if (!FixedTimeEquals(expectedMac, msgMac))
                throw new Exception("Media MAC validation failed");

            return CryptoUtils.AesCbcDecrypt(encryptedBytes, 0, cipherLen, keys.CipherKey, keys.IV);
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null || left.Length != right.Length) return false;

            int diff = 0;
            for (int i = 0; i < left.Length; i++)
            {
                diff |= left[i] ^ right[i];
            }
            return diff == 0;
        }

        public static async Task<byte[]> GenerateThumbnailAsync(IRandomAccessStream fileStream)
        {
            try
            {
                // Create decoder
                var decoder = await BitmapDecoder.CreateAsync(fileStream);

                // Resize to max 32px (standard WA thumbnail/micro-thumb)
                // WA usually wants a very small jpeg base64 for the 'jpegThumbnail' field.
                // 32x32 or similar.
                
                // Get pixel data slightly resized
                // Use a Transform to resize
                var transform = new BitmapTransform() { ScaledHeight = 32, ScaledWidth = 32, InterpolationMode = BitmapInterpolationMode.Fant };

                // Get pixels
                var pixelProvider = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8, 
                    BitmapAlphaMode.Premultiplied, 
                    transform, 
                    ExifOrientationMode.RespectExifOrientation, 
                    ColorManagementMode.DoNotColorManage);

                byte[] pixels = pixelProvider.DetachPixelData();

                // Encode to JPEG
                using (var ms = new InMemoryRandomAccessStream())
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
                    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, 32, 32, 96, 96, pixels);
                    await encoder.FlushAsync();

                    // Get bytes
                    var reader = new DataReader(ms.GetInputStreamAt(0));
                    byte[] result = new byte[ms.Size];
                    await reader.LoadAsync((uint)ms.Size);
                    reader.ReadBytes(result);
                    return result;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
