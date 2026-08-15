// =============================================================================
// IEncryptedMediaDownloader
//
// Fetches an encrypted blob from WhatsApp's media servers and hands back the
// plaintext.
//
// The socket layer knows which blob it wants and what to do with the bytes; it
// does not do HTTP, and the platform the host runs on decides how that happens.
// Key derivation and the MAC check sit on the host side of this line too, since
// they travel with the transfer rather than with the protocol.
//
// Ports: rc14 downloadContentFromMessage / downloadEncryptedContent in
// src/Utils/messages-media.ts
// =============================================================================
using System.Threading;
using System.Threading.Tasks;

namespace Unison.Socket.Abstractions
{
    /// <summary>Everything needed to locate and decrypt one blob.</summary>
    public sealed class EncryptedMediaRequest
    {
        /// <summary>Server-relative path. Preferred over <see cref="Url"/> when present.</summary>
        public string DirectPath { get; set; }

        public string Url { get; set; }

        public byte[] MediaKey { get; set; }

    /// <summary>
    /// WhatsApp's media type token, such as "md-msg-hist" or "image". It selects the HKDF
    /// info string the keys are expanded with; the wrong one fails the MAC check rather
    /// than producing garbage.
    /// </summary>
    public string MediaType { get; set; }

        /// <summary>Expected plaintext length, for validation. Zero when unknown.</summary>
        public long ExpectedLength { get; set; }
    }

    public interface IEncryptedMediaDownloader
    {
        /// <summary>
        /// Downloads, decrypts and verifies the blob. Throws when the transfer fails or the MAC
        /// does not match, because a partially decrypted blob is worse than none.
        /// </summary>
        Task<byte[]> DownloadAsync(
            EncryptedMediaRequest request,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
