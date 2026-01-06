using System;
using System.IO;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Runtime.InteropServices.WindowsRuntime;
using Unison.UWPApp.Crypto; // For CryptoUtils
using System.Text;

namespace Unison.UWPApp.Client
{
    public static class MediaUtils
    {
        // HKDF Info strings for different media types
        public const string IMAGE_HKDF_INFO = "WhatsApp Image Keys";
        public const string VIDEO_HKDF_INFO = "WhatsApp Video Keys";
        public const string AUDIO_HKDF_INFO = "WhatsApp Audio Keys";
        public const string DOCUMENT_HKDF_INFO = "WhatsApp Document Keys";

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
                case "video": infoStr = VIDEO_HKDF_INFO; break;
                case "audio": infoStr = AUDIO_HKDF_INFO; break;
                case "document": infoStr = DOCUMENT_HKDF_INFO; break;
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

            // 5. Calculate MAC
            // MAC = HMAC-SHA256(IV + EncryptedBody, MacKey).Substring(0, 10)
            byte[] ivPlusBody = new byte[keys.IV.Length + encryptedBody.Length];
            Array.Copy(keys.IV, 0, ivPlusBody, 0, keys.IV.Length);
            Array.Copy(encryptedBody, 0, ivPlusBody, keys.IV.Length, encryptedBody.Length);

            byte[] fullMac = CryptoUtils.HmacSha256(ivPlusBody, keys.MacKey);
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
