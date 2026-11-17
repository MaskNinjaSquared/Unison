using System.Collections.Generic;

namespace Unison.WhatsAppClient.Io.Messages
{
    /// <summary>Client-owned single-image send input.</summary>
    public sealed class SendImageRequest
    {
        public string ChatJid { get; set; }

        public byte[] ImageBytes { get; set; }

        public string Caption { get; set; }
    }

    /// <summary>Client-owned multi-image send; caption applies to the first image only.</summary>
    public sealed class SendImagesRequest
    {
        public string ChatJid { get; set; }

        public IReadOnlyList<byte[]> ImageBytes { get; set; }

        public string Caption { get; set; }
    }
}
